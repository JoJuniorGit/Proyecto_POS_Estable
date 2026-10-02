using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

/// <summary>
/// Mock HTTP del modo --e2e: la app WPF corre sin backend real. Cada respuesta debe respetar el
/// contrato real de los servicios cliente (shapes camelCase, enums numéricos, listas vs objetos);
/// un shape desalineado produce JsonException y diálogos de error visibles en la UI E2E.
/// </summary>
public class E2EMockHttpMessageHandler : HttpMessageHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private decimal _rate = 50.00m;
    private SaleState _sale = NewSale();
    private int _nextItemId = 1;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath.TrimEnd('/') ?? string.Empty;
        var query = request.RequestUri?.Query ?? string.Empty;
        var method = request.Method.Method.ToUpperInvariant();
        var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

        return Handle(method, path, query, body);
    }

    private HttpResponseMessage Handle(string method, string path, string query, string body)
    {
        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            return Json(new { status = "Healthy" });
        }

        if (path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            return Json(new
            {
                token = "e2e-mock-jwt-token",
                user = AdminUser(),
                requiresPasswordChange = false
            });
        }

        if (path.Equals("/api/auth/me", StringComparison.OrdinalIgnoreCase))
        {
            return Json(AdminUser());
        }

        if (path.Equals("/api/auth/change-password", StringComparison.OrdinalIgnoreCase))
        {
            return Json(new { ok = true });
        }

        if (path.Equals("/api/system/version-check", StringComparison.OrdinalIgnoreCase))
        {
            return Json(new
            {
                isClientCompatible = true,
                minimumClientVersion = "0.1.0",
                serverVersion = "0.1.0",
                updateServerUrl = string.Empty
            });
        }

        if (path.Equals("/api/exchange-rate/today", StringComparison.OrdinalIgnoreCase))
        {
            return Json(new { value = _rate, updatedAt = DateTime.UtcNow });
        }

        if (path.Equals("/api/exchange-rate/history", StringComparison.OrdinalIgnoreCase))
        {
            return Json(new[]
            {
                new { date = DateOnly.FromDateTime(DateTime.UtcNow), rate = _rate, updatedAt = DateTime.UtcNow }
            });
        }

        if (path.Equals("/api/exchange-rate/sync-bcv", StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate)
            {
                _rate = 50.00m;
            }
            return Json(new { value = _rate, updatedAt = DateTime.UtcNow });
        }

        if (path.Equals("/api/exchange-rate", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            lock (_gate)
            {
                var requested = ReadDecimal(body, "value");
                if (requested is > 0m)
                {
                    _rate = Math.Ceiling(requested.Value * 100m) / 100m;
                }
            }
            return Json(new { value = _rate, updatedAt = DateTime.UtcNow });
        }

        if (path.Equals("/api/PaymentMethods/active", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/PaymentMethods", StringComparison.OrdinalIgnoreCase))
        {
            return Json(PaymentMethods());
        }

        if (path.StartsWith("/api/products", StringComparison.OrdinalIgnoreCase))
        {
            return HandleProducts(method, path, body);
        }

        if (path.StartsWith("/api/sales", StringComparison.OrdinalIgnoreCase))
        {
            return HandleSales(method, path, query, body);
        }

        if (path.StartsWith("/api/cashdrawer", StringComparison.OrdinalIgnoreCase))
        {
            return HandleCashDrawer(method, path);
        }

        if (path.StartsWith("/api/dailyclosure", StringComparison.OrdinalIgnoreCase))
        {
            return HandleDailyClosure(method, path, body);
        }

        if (path.StartsWith("/api/settings", StringComparison.OrdinalIgnoreCase))
        {
            return HandleSettings(method, path, body);
        }

        if (path.StartsWith("/api/users", StringComparison.OrdinalIgnoreCase))
        {
            return HandleUsers(method, path, body);
        }

        if (path.StartsWith("/api/administration", StringComparison.OrdinalIgnoreCase))
        {
            return Json(new { ok = true });
        }

        // Fallback: 200 vacío para endpoints no usados por la UI. Los endpoints de lista que la UI
        // consume están cubiertos arriba; si se agrega uno nuevo, agregar su ruta con el shape real.
        return Json(new { });
    }

    // ── Productos ────────────────────────────────────────────────────────────────────────────

    private HttpResponseMessage HandleProducts(string method, string path, string body)
    {
        var segments = Segments(path);

        if (segments.Length == 2)
        {
            if (method == "POST")
            {
                return Json(Catalog[0]);
            }

            return Json(new { items = Catalog, totalCount = Catalog.Length, hasMore = false });
        }

        var sub = segments[2];
        if (sub.Equals("suggestions", StringComparison.OrdinalIgnoreCase))
        {
            return Json(Catalog);
        }

        if (sub.Equals("parents", StringComparison.OrdinalIgnoreCase))
        {
            return Json(Array.Empty<object>());
        }

        if (sub.Equals("quick-check", StringComparison.OrdinalIgnoreCase))
        {
            var sku = segments.Length > 3 ? segments[3] : string.Empty;
            var product = Catalog.FirstOrDefault(p =>
                p.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase) ||
                p.Barcode.Equals(sku, StringComparison.OrdinalIgnoreCase));
            return product == null ? Json(new { message = "Producto no encontrado." }, HttpStatusCode.NotFound) : Json(product);
        }

        if (sub.Equals("candidate-variants", StringComparison.OrdinalIgnoreCase))
        {
            return Json(new { items = Array.Empty<object>(), totalCount = 0, hasMore = false });
        }

        if (int.TryParse(sub, out var productId))
        {
            if (segments.Length > 3 && segments[3].Equals("variants", StringComparison.OrdinalIgnoreCase))
            {
                return Json(Array.Empty<object>());
            }

            var product = Catalog.FirstOrDefault(p => p.Id == productId);
            return product == null ? Json(new { message = "Producto no encontrado." }, HttpStatusCode.NotFound) : Json(product);
        }

        return Json(new { });
    }

    // ── Ventas ───────────────────────────────────────────────────────────────────────────────

    private HttpResponseMessage HandleSales(string method, string path, string query, string body)
    {
        var segments = Segments(path);
        if (segments.Length == 2)
        {
            return Json(new { items = Array.Empty<object>(), totalCount = 0 });
        }

        var sub = segments[2];
        if (sub.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate)
            {
                _sale = NewSale();
                _nextItemId = 1;
            }
            return Json(SaleJson());
        }

        if (sub.Equals("history", StringComparison.OrdinalIgnoreCase))
        {
            return Json(new { items = Array.Empty<object>(), totalCount = 0 });
        }

        if (sub.Equals("pending", StringComparison.OrdinalIgnoreCase) ||
            sub.Equals("pending-pickups", StringComparison.OrdinalIgnoreCase))
        {
            return Json(Array.Empty<object>());
        }

        if (sub.Equals("customers", StringComparison.OrdinalIgnoreCase))
        {
            return HandleCustomers(method, segments, body);
        }

        if (!int.TryParse(sub, out _))
        {
            return Json(new { });
        }

        if (segments.Length == 3)
        {
            return Json(SaleJson());
        }

        var action = segments[3].ToLowerInvariant();
        switch (action)
        {
            case "items":
                return HandleSaleItems(method, segments, body);

            case "exchange-rate":
                lock (_gate)
                {
                    if (TryParseQueryDecimal(query, "exchangeRate", out var rate) && rate > 0m)
                    {
                        _sale.AppliedRate = rate;
                    }
                }
                return Json(SaleJson());

            case "price-list":
                lock (_gate)
                {
                    _sale.PriceListType = ReadString(body, "priceListType") ?? "Retail";
                }
                return Json(SaleJson());

            case "customer":
                lock (_gate)
                {
                    _sale.CustomerId = ReadInt(body, "customerId") ?? 1;
                    _sale.CustomerName = "Consumidor Final";
                }
                return Json(SaleJson());

            case "complete":
                lock (_gate)
                {
                    _sale.Status = "Completed";
                }
                // El cliente hace int.Parse del cuerpo crudo: la API real devuelve el número de factura.
                return Text("42");

            case "hold":
                lock (_gate)
                {
                    _sale.Status = "OnHold";
                }
                return Json(SaleJson());

            case "claim":
            case "release":
            case "payments":
                return Json(SaleJson());

            case "confirm-pickup":
                return Json(new { ok = true });

            default:
                return Json(new { });
        }
    }

    private HttpResponseMessage HandleSaleItems(string method, string[] segments, string body)
    {
        // PUT /api/sales/{id}/items (edición masiva): 200 sin cuerpo relevante.
        if (segments.Length == 4)
        {
            if (method == "POST")
            {
                var productId = ReadInt(body, "productId") ?? 0;
                var quantity = ReadDecimal(body, "quantity") ?? 1m;
                var product = Catalog.FirstOrDefault(p => p.Id == productId);
                lock (_gate)
                {
                    var existing = _sale.Items.FirstOrDefault(i => i.ProductId == productId);
                    if (existing != null)
                    {
                        existing.Quantity += quantity;
                    }
                    else
                    {
                        _sale.Items.Add(new SaleItemState
                        {
                            Id = _nextItemId++,
                            ProductId = productId,
                            ProductName = product?.Name ?? $"Producto {productId}",
                            Quantity = quantity,
                            UnitPrice = product?.PriceRetailUSD ?? 0m
                        });
                    }
                }
                return Json(SaleJson());
            }

            return Json(new { ok = true });
        }

        // PUT/DELETE /api/sales/{id}/items/{itemId}
        if (segments.Length >= 5 && int.TryParse(segments[4], out var itemId))
        {
            lock (_gate)
            {
                var item = _sale.Items.FirstOrDefault(i => i.Id == itemId);
                if (method == "DELETE")
                {
                    if (item != null)
                    {
                        _sale.Items.Remove(item);
                    }
                }
                else if (method == "PUT" && item != null)
                {
                    item.Quantity = ReadDecimal(body, "quantity") ?? item.Quantity;
                }
            }
            return Json(SaleJson());
        }

        return Json(new { });
    }

    private HttpResponseMessage HandleCustomers(string method, string[] segments, string body)
    {
        if (segments.Length > 3 && segments[3].Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return Json(DefaultCustomer());
        }

        if (segments.Length > 3 && int.TryParse(segments[3], out _))
        {
            return method == "DELETE" ? Json(new { ok = true }) : Json(DefaultCustomer());
        }

        if (method == "POST")
        {
            return Json(DefaultCustomer());
        }

        return Json(new
        {
            items = new[] { DefaultCustomer() },
            totalCount = 1,
            page = 1,
            pageSize = 20
        });
    }

    private object SaleJson()
    {
        lock (_gate)
        {
            var items = _sale.Items.Select(i => new
            {
                id = i.Id,
                productId = i.ProductId,
                productName = i.ProductName,
                quantity = i.Quantity,
                unitPrice = i.UnitPrice,
                subtotal = Round2(i.Quantity * i.UnitPrice),
                unitPriceBsS = Ceil2(i.UnitPrice * _sale.AppliedRate),
                subtotalBsS = Round2(i.Quantity * Ceil2(i.UnitPrice * _sale.AppliedRate)),
                isFractional = false,
                unitOfMeasure = 0,
                isWholesaleApplied = false,
                isCustomPrice = false
            }).ToList();

            var subtotal = Round2(_sale.Items.Sum(i => i.Quantity * i.UnitPrice));
            var subtotalBsS = Round2(_sale.Items.Sum(i => i.Quantity * Ceil2(i.UnitPrice * _sale.AppliedRate)));

            return new
            {
                id = _sale.Id,
                invoiceNumber = (int?)null,
                date = DateTime.UtcNow,
                status = _sale.Status,
                subtotal,
                totalUSD = subtotal,
                appliedRate = _sale.AppliedRate,
                totalBsS = subtotalBsS,
                subtotalBsS,
                roundingAdjustment = 0m,
                finalPaidAmountBsS = 0m,
                cashierId = 1,
                cashierName = "Administrador E2E",
                customerId = _sale.CustomerId,
                customerName = _sale.CustomerName,
                customerCedula = _sale.CustomerCedula,
                deliveryStatus = "Delivered",
                priceListType = _sale.PriceListType,
                totalPaidUSD = 0m,
                remainingBalanceUSD = subtotal,
                items,
                payments = Array.Empty<object>()
            };
        }
    }

    // ── Caja ─────────────────────────────────────────────────────────────────────────────────

    private static HttpResponseMessage HandleCashDrawer(string method, string path)
    {
        var segments = Segments(path);
        var sub = segments.Length > 2 ? segments[2].ToLowerInvariant() : string.Empty;

        if (sub == "active-session")
        {
            return Json(new
            {
                id = 1,
                openedAt = DateTime.UtcNow.AddHours(-2),
                closedAt = (DateTime?)null,
                status = 0,
                openingBalanceLocal = 5000.00m,
                openingExchangeRate = 50.00m,
                closingBalanceLocal = (decimal?)null,
                closingExchangeRate = (decimal?)null,
                transactions = Transactions()
            });
        }

        if (sub == "history")
        {
            return Json(Transactions());
        }

        if (sub == "current-balance")
        {
            return Json(7500.00m);
        }

        if (sub == "advance-commission")
        {
            return Json(new { isTransfer = false, percentage = 0m });
        }

        if (sub == "open" || sub == "close")
        {
            return Json(new
            {
                id = 1,
                openedAt = DateTime.UtcNow.AddHours(-2),
                closedAt = (DateTime?)null,
                status = 0,
                openingBalanceLocal = 5000.00m,
                openingExchangeRate = 50.00m,
                closingBalanceLocal = (decimal?)null,
                closingExchangeRate = (decimal?)null,
                transactions = Transactions()
            });
        }

        if (sub == "transaction")
        {
            return Json(Transactions()[0]);
        }

        if (sub == "cash-advance")
        {
            return Json(new
            {
                expenseTransaction = Transactions()[0],
                incomeTransaction = Transactions()[1],
                requestedAmountLocal = 1000.00m,
                commissionAmountLocal = 0m,
                totalChargedLocal = 1000.00m,
                commissionPercentage = 0m,
                relatedSaleId = (int?)null,
                invoiceNumber = (int?)null
            });
        }

        return Json(new { });
    }

    private static object[] Transactions() => new object[]
    {
        new
        {
            id = 1,
            transactionTimeLocal = DateTime.UtcNow.AddHours(-1),
            description = "Apertura de caja",
            invoiceNumber = (int?)null,
            amountUsd = 100.00m,
            amountLocal = 5000.00m,
            exchangeRate = 50.00m,
            type = 0,
            source = 0,
            isPhysicalCash = true,
            paymentMethodId = (int?)null
        },
        new
        {
            id = 2,
            transactionTimeLocal = DateTime.UtcNow.AddMinutes(-30),
            description = "Venta POS",
            invoiceNumber = 42,
            amountUsd = 50.00m,
            amountLocal = 2500.00m,
            exchangeRate = 50.00m,
            type = 0,
            source = 1,
            isPhysicalCash = true,
            paymentMethodId = 1
        }
    };

    // ── Cierre diario ────────────────────────────────────────────────────────────────────────

    private static HttpResponseMessage HandleDailyClosure(string method, string path, string body)
    {
        if (path.Contains("expected-totals", StringComparison.OrdinalIgnoreCase))
        {
            return Json(new[]
            {
                new { paymentMethodId = 1, paymentMethodName = "Efectivo Bs.S", expectedAmountBsS = 2500.00m },
                new { paymentMethodId = 2, paymentMethodName = "Efectivo USD", expectedAmountBsS = 5000.00m },
                new { paymentMethodId = 3, paymentMethodName = "Pago Móvil", expectedAmountBsS = 0.00m }
            });
        }

        if (method == "POST")
        {
            return Json(new
            {
                id = 1,
                closureDate = DateTime.UtcNow,
                userId = "admin",
                totalExpectedBsS = 7500.00m,
                totalActualBsS = 7500.00m,
                totalDifferenceBsS = 0.00m,
                observation = (string?)null,
                details = Array.Empty<object>()
            });
        }

        return Json(new { });
    }

    // ── Ajustes y usuarios ───────────────────────────────────────────────────────────────────

    private static HttpResponseMessage HandleSettings(string method, string path, string body)
    {
        if (path.Contains("currency-format", StringComparison.OrdinalIgnoreCase))
        {
            return method == "GET" ? Json(new { format = "Venezuelan" }) : Json(new { ok = true });
        }

        if (path.Contains("timezone", StringComparison.OrdinalIgnoreCase))
        {
            return method == "GET" ? Json(new { id = "America/Caracas" }) : Json(new { ok = true });
        }

        if (path.Contains("allow-negative-stock", StringComparison.OrdinalIgnoreCase))
        {
            return method == "GET" ? Json(new { allowed = false }) : Json(new { ok = true });
        }

        return Json(new { });
    }

    private static HttpResponseMessage HandleUsers(string method, string path, string body)
    {
        if (method == "GET")
        {
            return Json(new[] { AdminUser() });
        }

        if (method == "POST")
        {
            return Json(new
            {
                id = 2,
                cedula = ReadString(body, "cedula") ?? "nuevo",
                name = ReadString(body, "name") ?? "Usuario Nuevo",
                role = 1,
                isActive = true,
                temporaryPassword = "Temporal123",
                mustChangePassword = true
            });
        }

        return Json(AdminUser());
    }

    // ── Datos base ───────────────────────────────────────────────────────────────────────────

    private static object AdminUser() => new
    {
        id = 1,
        cedula = "admin",
        name = "Administrador E2E",
        role = 3,
        isActive = true
    };

    private static object DefaultCustomer() => new
    {
        id = 1,
        cedulaOrRif = "V-00000000",
        name = "Consumidor Final",
        phone = string.Empty,
        creditLimitUSD = 0.00m,
        isActive = true,
        isDefault = true
    };

    private static object[] PaymentMethods() => new object[]
    {
        new { id = 1, name = "Efectivo Bs.S", isActive = true, requiresReference = false, isCash = true, currency = "Bs.S", displayOrder = 1, isDeleted = false },
        new { id = 2, name = "Efectivo USD", isActive = true, requiresReference = false, isCash = true, currency = "USD", displayOrder = 2, isDeleted = false },
        new { id = 3, name = "Pago Móvil", isActive = true, requiresReference = true, isCash = false, currency = "Bs.S", displayOrder = 3, isDeleted = false }
    };

    private static readonly ProductMock[] Catalog =
    {
        new(1, "7591001", "7591001", "Harina PAN 1kg", 1.50m, 1.35m, 1.00m, 75.00m, 100m, 10m, true, 6m),
        new(2, "7591002", "7591002", "Arroz Primor 1kg", 1.20m, 1.05m, 0.80m, 60.00m, 80m, 10m, true, 6m),
        new(3, "7591003", "7591003", "Aceite Vatel 1L", 2.80m, 2.50m, 2.00m, 140.00m, 40m, 5m, false, 6m)
    };

    private static SaleState NewSale() => new()
    {
        Id = 1,
        Status = "Pending",
        AppliedRate = 50.00m,
        PriceListType = "Retail",
        CustomerId = 1,
        CustomerName = "Consumidor Final",
        CustomerCedula = "V-00000000"
    };

    // ── Infraestructura ──────────────────────────────────────────────────────────────────────

    private static string[] Segments(string path) =>
        path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static HttpResponseMessage Json(object payload, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "text/plain")
    };

    private static string? ReadString(string body, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(property, out var value)
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadInt(string body, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
                ? number
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static decimal? ReadDecimal(string body, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(property, out var value) && value.TryGetDecimal(out var number)
                ? number
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseQueryDecimal(string query, string key, out decimal value)
    {
        value = 0m;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return decimal.TryParse(kv[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value);
            }
        }
        return false;
    }

    private static decimal Ceil2(decimal amount) => Math.Ceiling(amount * 100m) / 100m;

    private static decimal Round2(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    private sealed record ProductMock(
        int Id,
        string Sku,
        string Barcode,
        string Name,
        decimal PriceRetailUSD,
        decimal PriceWholesaleUSD,
        decimal CostPriceUSD,
        decimal PriceBsS,
        decimal StockQuantity,
        decimal LowStockThreshold,
        bool HasWholesale,
        decimal MinWholesaleQuantity)
    {
        public decimal PriceUSD => PriceRetailUSD;
        public decimal ProfitPercentage => 0m;
        public bool IsActive => true;
        public bool IsFractional => false;
        public decimal ReservedQuantity => 0m;
        public int? ParentProductId => null;
        public bool IsGroupHeader => false;
        public bool HasIndependentPricing => false;
        public int VariantCount => 0;
        public decimal ConsolidatedStock => StockQuantity;
    }

    private sealed class SaleState
    {
        public int Id { get; init; }
        public string Status { get; set; } = "Pending";
        public decimal AppliedRate { get; set; } = 50.00m;
        public string PriceListType { get; set; } = "Retail";
        public int? CustomerId { get; set; }
        public string CustomerName { get; set; } = "Consumidor Final";
        public string CustomerCedula { get; set; } = "V-00000000";
        public List<SaleItemState> Items { get; } = new();
    }

    private sealed class SaleItemState
    {
        public int Id { get; init; }
        public int ProductId { get; init; }
        public string ProductName { get; init; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; init; }
    }
}
