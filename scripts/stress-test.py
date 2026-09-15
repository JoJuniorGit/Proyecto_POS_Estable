import argparse
import json
import math
import os
import random
import ssl
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


class HttpError(Exception):
    def __init__(self, status, body):
        super().__init__(f"HTTP {status}: {body[:300]}")
        self.status = status
        self.body = body


class ApiClient:
    def __init__(self, base_url, token, timeout, insecure):
        self.base = base_url.rstrip("/")
        self.token = token
        self.timeout = timeout
        self.ctx = ssl._create_unverified_context() if insecure else None
        self.headers = {
            "Content-Type": "application/json",
            "User-Agent": "pos-stress-test/8.108",
        }
        self.apply_token(token)

    def apply_token(self, token):
        self.token = token
        token_header = {"Authorization": f"Bearer {token}"} if token else {}
        self.headers.update(token_header)

    def _request(self, method, path, body=None, extra_headers=None):
        url = f"{self.base}{path}"
        data = json.dumps(body).encode("utf-8") if body is not None else None
        headers = dict(self.headers)
        if extra_headers:
            headers.update(extra_headers)
        req = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=self.timeout, context=self.ctx) as resp:
                raw = resp.read()
                if not raw:
                    return None
                try:
                    return json.loads(raw.decode("utf-8"))
                except ValueError:
                    return raw.decode("utf-8")
        except urllib.error.HTTPError as exc:
            body = ""
            try:
                body = exc.read().decode("utf-8")
            except Exception:
                pass
            raise HttpError(exc.code, body) from exc

    def get(self, path):
        return self._request("GET", path)

    def post(self, path, body=None, extra_headers=None):
        return self._request("POST", path, body, extra_headers)


class Metrics:
    def __init__(self):
        self._lock = threading.Lock()
        self.latencies = {}
        self.status = {}
        self.ok = {}
        self.fail = {}

    def record(self, label, seconds, status_code):
        with self._lock:
            self.latencies.setdefault(label, []).append(seconds)
            dist = self.status.setdefault(label, {})
            dist[status_code] = dist.get(status_code, 0) + 1
            if 200 <= status_code < 300:
                self.ok[label] = self.ok.get(label, 0) + 1
            else:
                self.fail[label] = self.fail.get(label, 0) + 1

    def totals(self):
        with self._lock:
            result = {}
            for label, values in self.latencies.items():
                sorted_values = sorted(values)
                avg = (sum(values) / len(values)) if values else 0.0
                result[label] = {
                    "n": len(values),
                    "ok": self.ok.get(label, 0),
                    "fail": self.fail.get(label, 0),
                    "status": dict(self.status[label]),
                    "avgMs": round(avg * 1000.0, 1),
                    "p95Ms": round(percentile(sorted_values, 95) * 1000.0, 1),
                    "p99Ms": round(percentile(sorted_values, 99) * 1000.0, 1),
                }
            return result


class Budget:
    def __init__(self, total):
        self.total = total
        self.remaining = total
        self._lock = threading.Lock()

    def acquire_one(self):
        if self.total is None:
            return True
        with self._lock:
            if self.remaining <= 0:
                return False
            self.remaining -= 1
            return True


class Counters:
    def __init__(self):
        self._lock = threading.Lock()
        self.completed = 0
        self.sale_failures = 0
        self.relogins = 0

    def add_completed(self, value=1):
        with self._lock:
            self.completed += value

    def add_sale_failure(self, value=1):
        with self._lock:
            self.sale_failures += value

    def add_relogin(self, value=1):
        with self._lock:
            self.relogins += value

    def snapshot(self):
        with self._lock:
            return (self.completed, self.sale_failures, self.relogins)


def parse_args(argv):
    parser = argparse.ArgumentParser(
        prog="stress-test.py",
        description="Prueba de estrés no destructiva del CommandCenter POS. Simula N cajas "
                    "concurrentes ejecutando ventas de $0.00 sobre productos sintéticos SKU-TEST-* "
                    "y mide latencia, rendimiento y fallos por endpoint.",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    parser.add_argument("--base-url", required=True,
                        help="URL raíz del backend de staging (ej. http://192.168.1.50:5000). "
                             "Obligatorio y sin valor por defecto.")
    parser.add_argument("--confirm-staging", required=True,
                        help="Debe repetir literalmente el valor de --base-url (o 'YES') para "
                             "confirmar que apunta a staging y no a producción.")
    parser.add_argument("--user", default="BOT_STRESS_TEST",
                        help="Cédula/usuario del cajero de aislamiento.")
    parser.add_argument("--password", default=None,
                        help="Contraseña del usuario de aislamiento (obsoleto: preferir la variable de entorno "
                             "POS_STRESS_PASSWORD para no exponerla en la lista de procesos).")
    parser.add_argument("--cashiers", type=int, default=4,
                        help="Número de cajas (hilos) concurrentes.")
    parser.add_argument("--duration", type=int, default=None,
                        help="Duración total en segundos.")
    parser.add_argument("--transactions", type=int, default=None,
                        help="Número total de ventas a completar entre todos los hilos.")
    parser.add_argument("--think-min", type=float, default=4.0,
                        help="Tiempo de espera mínimo entre ventas (segundos).")
    parser.add_argument("--think-max", type=float, default=20.0,
                        help="Tiempo de espera máximo entre ventas (segundos).")
    parser.add_argument("--filter", default="SKU-TEST",
                        help="Prefijo/filtro de SKU para descubrir los productos de prueba.")
    parser.add_argument("--products", nargs="*", default=None,
                        help="SKUs exactos a usar (por defecto: todos los del filtro).")
    parser.add_argument("--qty-min", type=int, default=1,
                        help="Cantidad mínima por artículo.")
    parser.add_argument("--qty-max", type=int, default=20,
                        help="Cantidad máxima por artículo (acotada al stock del producto).")
    parser.add_argument("--max-products-per-sale", type=int, default=None,
                        help="Máximo de productos distintos por venta (por defecto: todos).")
    parser.add_argument("--rate", type=float, default=None,
                        help="Tasa Bs/USD fija (por defecto se lee de /api/exchange-rate/today).")
    parser.add_argument("--timeout", type=float, default=60.0,
                        help="Timeout por petición HTTP (segundos).")
    parser.add_argument("--insecure", action="store_true",
                        help="Desactiva la verificación TLS (para staging con certificado propio).")
    parser.add_argument("--out", default=None,
                        help="Ruta opcional a un archivo JSON con el resumen de resultados.")
    parser.add_argument("--stop-file", default=None,
                        help="Ruta opcional a un archivo centinela; al aparecer, la prueba "
                             "finaliza de forma ordenada y consolida los resultados parciales.")
    args = parser.parse_args(argv)

    if args.base_url.rstrip("/") != args.confirm_staging and args.confirm_staging != "YES":
        parser.error("--confirm-staging debe repetir el valor de --base-url (o 'YES').")

    if not args.duration and not args.transactions:
        parser.error("Debe especificar --duration (segundos) o --transactions (ventas totales).")

    if args.cashiers < 1:
        parser.error("--cashiers debe ser >= 1.")

    if args.qty_min < 1 or args.qty_max < args.qty_min:
        parser.error("Rango de cantidades inválido (--qty-min >= 1 y --qty-max >= --qty-min).")

    if args.think_min < 0 or args.think_max < args.think_min:
        parser.error("Rango de think time inválido (--think-min >= 0 y --think-max >= --think-min).")

    if not args.password:
        args.password = os.environ.get("POS_STRESS_PASSWORD", "")
    if not args.password:
        parser.error("Falta la contraseña del usuario de aislamiento: defina POS_STRESS_PASSWORD "
                     "(o use --password).")

    return args


def stop_requested(path):
    return bool(path) and os.path.exists(path)


def fetch_json(client, path):
    return client.get(path)


def login(client, user, password):
    result = client.post(
        "/api/auth/login",
        {"cedula": user, "password": password, "platform": "Desktop"},
    )
    if not result or not result.get("token"):
        require_change = bool(result and result.get("requiresPasswordChange"))
        message = result.get("message") if result else "respuesta vacía del servidor"
        if require_change:
            sys.exit(f"ERROR: {message} El usuario {user} debe cambiar su contraseña antes de usar el script.")
        sys.exit(f"ERROR: No se pudo iniciar sesión como {user}: {message}")
    return result


def fetch_products(client, name_filter, max_pages=10, page_size=100):
    products = []
    total = None
    page = 1
    while page <= max_pages:
        query = urllib.parse.urlencode({"filter": name_filter, "page": page, "pageSize": page_size})
        data = client.get(f"/api/products?{query}")
        items = data.get("items") or data.get("Items") or []
        if total is None:
            total = data.get("totalCount") or data.get("TotalCount") or 0
        products.extend(items)
        if len(items) < page_size:
            break
        if total and len(products) >= total:
            break
        page += 1
    return products


def build_test_products(all_products, requested_skus, qty_max, sku_filter):
    by_sku = {}
    for p in all_products:
        sku = (p.get("sku") or "").strip()
        if sku:
            by_sku.setdefault(sku.lower(), p)
    selected = []
    if requested_skus:
        missing = []
        for sku in requested_skus:
            product = by_sku.get(sku.strip().lower())
            if product is None:
                missing.append(sku)
                continue
            selected.append(product)
        if missing:
            sys.exit(f"ERROR: Los siguientes productos de prueba no se encontraron en staging: {', '.join(missing)}")
    else:
        selected = list(all_products)

    test_products = []
    for p in selected:
        price_usd = float(p.get("priceUsd") or p.get("priceRetailUsd") or 0)
        price_retail = float(p.get("priceRetailUsd") or 0)
        if price_usd != 0 or price_retail != 0:
            sys.exit(f"ERROR: El producto {p.get('sku')} no tiene precio $0.00 (priceUsd={price_usd}, "
                     f"priceRetailUsd={price_retail}). Solo se permiten productos de $0.00 en la prueba.")
        if not p.get("isActive"):
            sys.exit(f"ERROR: El producto {p.get('sku')} está inactivo. Debe activarse en staging.")
        if p.get("isDeleted"):
            sys.exit(f"ERROR: El producto {p.get('sku')} está eliminado (soft delete).")
        stock = float(p.get("stockQuantity") or 0)
        test_products.append(
            {
                "id": p.get("id"),
                "sku": (p.get("sku") or "").strip(),
                "name": p.get("name") or "",
                "priceUsd": price_usd,
                "priceBsS": float(p.get("priceBsS") or 0),
                "stockQuantity": stock,
                "safeQtyMax": min(qty_max, max(0, int(stock))),
            }
        )
    if not test_products:
        sys.exit(f"ERROR: No se encontraron productos con el filtro '{sku_filter}'. "
                 "Cree los productos sintéticos SKU-TEST-* en staging antes de ejecutar.")
    return test_products


def print_products(preview_header, test_products, all_matched):
    print(preview_header)
    print("-" * 100)
    print(f"{'SKU':<18}{'ID':<8}{'Precio USD':<12}{'Precio Bs.S':<16}{'Stock':<10}{'Qty máx segura':<16}Nombre")
    print("-" * 100)
    for p in test_products:
        print(f"{p['sku']:<18}{p['id']:<8}{p['priceUsd']:<12.2f}{p['priceBsS']:<16.2f}"
              f"{p['stockQuantity']:<10.0f}{p['safeQtyMax']:<16d}{p['name']}")
    print("-" * 100)
    extra = [p for p in all_matched if p not in {t["sku"] for t in test_products}]
    if extra:
        print(f"Otros productos descubiertos con el filtro (no usados): {', '.join(sorted(extra))}")
    low_stock = [p for p in test_products if p["safeQtyMax"] < 5]
    if low_stock:
        print("ADVERTENCIA: productos con stock bajo que limitarán las cantidades de la prueba: "
              + ", ".join(p["sku"] for p in low_stock))
        print("Se recomienda reponer stock (>> 50.000) en staging para corridas largas.")


def timed_call(client, metrics, label, fn):
    start = time.perf_counter()
    try:
        result = fn()
        metrics.record(label, time.perf_counter() - start, 200)
        return result
    except HttpError as exc:
        metrics.record(label, time.perf_counter() - start, exc.status)
        raise
    except urllib.error.URLError as exc:
        metrics.record(label, time.perf_counter() - start, 0)
        raise
    except Exception as exc:
        metrics.record(label, time.perf_counter() - start, 0)
        raise


def requires_relogin(exc):
    if isinstance(exc, HttpError):
        return exc.status in (401, 403)
    return False


def run_one_sale(client, token_state, user, password, test_products, rate, max_products,
                 metrics, counters):
    start = client
    try:
        result = timed_call(start, metrics, "start", lambda: start.post("/api/sales/start"))
    except Exception as exc:
        if requires_relogin(exc):
            counters.add_relogin()
            with token_state["lock"]:
                refreshed = login(client, user, password)
                token_state["token"] = refreshed["token"]
                client.apply_token(refreshed["token"])
            try:
                result = timed_call(start, metrics, "start", lambda: start.post("/api/sales/start"))
            except Exception as retry_exc:
                counters.add_sale_failure()
                return
        else:
            counters.add_sale_failure()
            return

    sale_id = result.get("id")
    if not sale_id:
        counters.add_sale_failure()
        return

    count = random.randint(1, len(test_products))
    if max_products:
        count = min(count, max_products)
    picked = random.sample(test_products, count)

    cycle_start = time.perf_counter()
    all_items_ok = True
    for product in picked:
        qty = random.randint(1, product["safeQtyMax"]) if product["safeQtyMax"] > 0 else 0
        if qty <= 0:
            continue
        try:
            timed_call(
                start,
                metrics,
                "items",
                lambda p=product, q=qty: start.post(
                    f"/api/sales/{sale_id}/items",
                    {"productId": p["id"], "quantity": q, "exchangeRate": rate},
                ),
            )
        except Exception as exc:
            all_items_ok = False
            if requires_relogin(exc):
                with token_state["lock"]:
                    refreshed = login(client, user, password)
                    token_state["token"] = refreshed["token"]
                    client.apply_token(refreshed["token"])
                    counters.add_relogin()
            break

    if not all_items_ok:
        counters.add_sale_failure()
        return

    try:
        timed_call(
            start,
            metrics,
            "checkout-preview",
            lambda: start.post(
                f"/api/sales/{sale_id}/checkout-preview",
                {"exchangeRate": rate, "payments": []},
            ),
        )
        idempotency_key = str(uuid.uuid4())
        timed_call(
            start,
            metrics,
            "complete",
            lambda: start.post(
                f"/api/sales/{sale_id}/complete",
                {
                    "exchangeRate": rate,
                    "roundingAdjustment": 0,
                    "isPendingPickup": False,
                    "payments": [],
                },
                {"Idempotency-Key": idempotency_key},
            ),
        )
        metrics.record("cycle", time.perf_counter() - cycle_start, 200)
        counters.add_completed()
    except Exception as exc:
        counters.add_sale_failure()
        if requires_relogin(exc):
            with token_state["lock"]:
                refreshed = login(client, user, password)
                token_state["token"] = refreshed["token"]
                client.apply_token(refreshed["token"])
                counters.add_relogin()


def worker(client, token_state, user, password, test_products, rate, max_products,
           metrics, counters, budget, stop_event, think_min, think_max, barrier):
    barrier.wait()
    while not stop_event.is_set():
        if not budget.acquire_one():
            break
        run_one_sale(client, token_state, user, password, test_products, rate, max_products,
                     metrics, counters)
        time.sleep(random.uniform(think_min, think_max))


def percentile(sorted_values, pct):
    if not sorted_values:
        return 0.0
    index = max(0, int(math.ceil(pct / 100.0 * len(sorted_values))) - 1)
    return sorted_values[index]


def format_ms(seconds):
    return f"{seconds * 1000:.1f} ms"


def print_summary(elapsed, metrics, counters, num_cashiers):
    completed, sale_failures, relogins = counters.snapshot()
    print()
    print("=" * 100)
    print("RESUMEN DE LA PRUEBA DE ESTRÉS")
    print("=" * 100)
    print(f"Duración: {elapsed:.1f} s | Cajas simuladas: {num_cashiers} | Ventas completadas: {completed}"
          f" | Fallos de venta: {sale_failures} | Re-logins: {relogins}")
    if elapsed > 0:
        print(f"Rendimiento: {completed / (elapsed / 60.0):.2f} ventas/min | Tiempo medio por venta: "
              f"{elapsed / completed if completed else 0:.2f} s (con think time)")
    print()
    print(f"{'Endpoint':<18}{'N':>8}{'OK':>8}{'FAIL':>8}{'Avg':>14}{'p95':>14}{'p99':>14}"
          "   Distribución de códigos HTTP")
    print("-" * 100)

    totals = metrics.totals()
    all_labels = [name for name, _ in sorted(totals.items(), key=lambda item: item[1]["n"], reverse=True)]
    for label in all_labels:
        info = totals[label]
        times = sorted(metrics.latencies[label])
        avg = (sum(times) / len(times)) if times else 0.0
        status_str = ", ".join(f"{code}:{count}" for code, count in sorted(info["status"].items()))
        print(f"{label:<18}{info['n']:>8}{info['ok']:>8}{info['fail']:>8}"
              f"{format_ms(avg):>14}{format_ms(percentile(times, 95)):>14}"
              f"{format_ms(percentile(times, 99)):>14}   {status_str}")
    print("=" * 100)
    if sale_failures:
        print("NOTA: revise los códigos HTTP. 429 indica throttling por GeneralApiRateLimit "
              "(200 req/min por IP).")
    print("Para medir la capacidad real del backend con 4 cajas remotas, use 4 máquinas/IPs "
          "distintas o ajuste el límite en staging.")


def write_json_out(path, payload):
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2)


def main(argv):
    args = parse_args(argv)

    print(f"Pre-flight contra {args.base_url}")
    anonymous = ApiClient(args.base_url, "", args.timeout, args.insecure)

    health = fetch_json(anonymous, "/health")
    if isinstance(health, dict) and health.get("status"):
        print(f"/health -> {health.get('status')} (database: {health.get('database')})")
    else:
        print("AVISO: /health no devolvió el shape esperado (status/database).")

    if args.rate:
        rate = args.rate
        print(f"Tasa forzada: {rate}")
    else:
        today = fetch_json(anonymous, "/api/exchange-rate/today")
        rate = float(today.get("value") or 0) if isinstance(today, dict) else 0.0
        if rate <= 0:
            sys.exit("ERROR: La tasa BCV del día es 0 en staging. Cárguela antes de la prueba "
                     "(/api/exchange-rate o la app).")
        print(f"Tasa BCV del día: {rate}")

    login_result = login(anonymous, args.user, args.password)
    token = login_result["token"]
    client = ApiClient(args.base_url, token, args.timeout, args.insecure)
    print(f"Sesión iniciada como {args.user} (id={login_result.get('user', {}).get('id')}).")

    found_products = fetch_products(client, args.filter)
    test_products = build_test_products(found_products, args.products, args.qty_max, args.filter)
    print_products("PRODUCTOS DE PRUEBA DESTINADOS AL ESTRÉS", test_products, [p.get("sku") for p in found_products])

    if not args.products:
        print(f"Usando todos los {len(test_products)} productos descubiertos con el filtro "
              f"'{args.filter}'.")

    stop_event = threading.Event()
    budget = Budget(args.transactions)
    counters = Counters()
    metrics = Metrics()
    token_state = {"token": token, "lock": threading.Lock()}
    barrier = threading.Barrier(args.cashiers)

    threads = []
    for _ in range(args.cashiers):
        thread = threading.Thread(
            target=worker,
            args=(client, token_state, args.user, args.password, test_products, rate,
                  args.max_products_per_sale, metrics, counters, budget, stop_event,
                  args.think_min, args.think_max, barrier),
            daemon=True,
        )
        thread.start()
        threads.append(thread)

    started_at = time.monotonic()
    try:
        if args.duration:
            deadline = started_at + args.duration
            while time.monotonic() < deadline and not stop_requested(args.stop_file):
                time.sleep(0.5)
        else:
            while budget.remaining > 0 and not stop_requested(args.stop_file):
                time.sleep(0.5)
        stop_event.set()
    except KeyboardInterrupt:
        print("\nInterrupción recibida. Cerrando hilos...")
        stop_event.set()

    if stop_requested(args.stop_file):
        print("\nFinalización solicitada: cerrando hilos y consolidando resultados parciales...")

    for thread in threads:
        thread.join(timeout=30)

    elapsed = time.monotonic() - started_at
    totals = metrics.totals()

    print_summary(elapsed, metrics, counters, args.cashiers)

    if args.out:
        completed, sale_failures, relogins = counters.snapshot()
        payload = {
            "baseUrl": args.base_url,
            "user": args.user,
            "cashiers": args.cashiers,
            "elapsedSeconds": round(elapsed, 3),
            "rate": rate,
            "completedSales": completed,
            "saleFailures": sale_failures,
            "relogins": relogins,
            "products": test_products,
            "metrics": totals,
        }
        write_json_out(args.out, payload)
        print(f"Resultados escritos en {args.out}")


if __name__ == "__main__":
    main(sys.argv[1:])