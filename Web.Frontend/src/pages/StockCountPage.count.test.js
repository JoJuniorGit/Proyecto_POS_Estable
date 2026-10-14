import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  getStatusMeta,
  buildStatusMeta,
  canManageStockCount,
  formatSignedQuantity,
  getDifferenceTone,
  resolvePendingScanRequest,
} from './StockCountPage.jsx';

/**
 * 8.180-T2 (S5/S3): contrato de la vista de conteo físico ciego. Se cubren los helpers puros
 * (estados, gating de rol, Δ con signo) y el blindaje por source-scan: el flujo de conteo no
 * puede referenciar teórico/costo/valores — eso vive solo en el panel review Admin/Manager.
 */
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'StockCountPage.jsx'), 'utf-8');
const normalizedSource = source.replace(/\s+/g, ' ');

describe('StockCountPage — conteo físico ciego (8.180-T2, S5)', () => {
  it('1. El mapa de estados 0-3 usa labels ES y tolera nombres/valores desconocidos', () => {
    assert.strictEqual(getStatusMeta(0).label, 'Abierta');
    assert.strictEqual(getStatusMeta(1).label, 'Cerrada');
    assert.strictEqual(getStatusMeta(2).label, 'Aplicada');
    assert.strictEqual(getStatusMeta(3).label, 'Cancelada');
    assert.strictEqual(getStatusMeta('2').tone, 'applied');
    assert.strictEqual(getStatusMeta('Open').label, 'Abierta');
    assert.strictEqual(getStatusMeta(99).label, 'Desconocida');
    assert.strictEqual(getStatusMeta(undefined).tone, 'unknown');
  });

  it('2. El estado agrega contexto con closedAt/appliedAt (appliedAt manda)', () => {
    assert.strictEqual(buildStatusMeta({ status: 0 }).context, null);

    const closed = buildStatusMeta({ status: 1, closedAt: '2026-10-13T11:16:41' });
    assert.strictEqual(closed.label, 'Cerrada');
    assert.ok(closed.context.startsWith('13/10/2026'), closed.context);
    assert.ok(closed.context.includes(':'), 'incluye hora de cierre');

    const applied = buildStatusMeta({
      status: 2,
      closedAt: '2026-10-01T10:00:00',
      appliedAt: '2026-10-13T11:16:41',
    });
    assert.strictEqual(applied.label, 'Aplicada');
    assert.ok(applied.context.startsWith('13/10/2026'), applied.context);
  });

  it('3. Solo Admin/Manager gestionan (crear, cerrar, aplicar, cancelar, review)', () => {
    for (const role of ['Admin', 'Manager', 3, 2]) {
      assert.strictEqual(canManageStockCount(role), true, `role ${role}`);
    }
    for (const role of ['Cashier', 'Driver', 1, 4, null, undefined, 'Supervisor']) {
      assert.strictEqual(canManageStockCount(role), false, `role ${role}`);
    }
  });

  it('4. formatSignedQuantity marca el signo y getDifferenceTone el color del Δ', () => {
    assert.strictEqual(formatSignedQuantity(2.5), '+2.5');
    assert.strictEqual(formatSignedQuantity(-3), '-3');
    assert.strictEqual(formatSignedQuantity(0), '0');
    assert.strictEqual(formatSignedQuantity(null), '0');

    assert.strictEqual(getDifferenceTone(1), 'surplus');
    assert.strictEqual(getDifferenceTone(-0.5), 'shortage');
    assert.strictEqual(getDifferenceTone(0), 'even');
  });

  it('5. BLIND: el flujo de conteo (antes del panel review) no referencia teórico/costo/valores', () => {
    const parts = normalizedSource.split('REVIEW_PANEL_START');
    assert.strictEqual(parts.length, 2, 'debe existir exactamente un marcador REVIEW_PANEL_START');

    const blindSection = parts[0].toLowerCase();
    const reviewSection = parts[1].toLowerCase();
    for (const leak of [
      'theoreticalstock',
      'unitcost',
      'differencevalue',
      'netvalueusd',
      'totalsurplus',
      'totalshortage',
    ]) {
      assert.ok(!blindSection.includes(leak), `el conteo ciego no debe referenciar ${leak}`);
    }

    assert.ok(reviewSection.includes('theoreticalstock'), 'el panel review sí muestra el teórico');

    // Fix verificación: el contrato camelCase preserva el acrónimo (UnitCostUSD → unitCostUSD);
    // asertar case-sensitive para no repetir el defecto del casing incorrecto.
    const reviewSectionRaw = normalizedSource.split('REVIEW_PANEL_START')[1];
    assert.ok(reviewSectionRaw.includes('unitCostUSD'), 'el panel review debe consumir unitCostUSD (camelCase exacto del contrato)');
    assert.ok(!reviewSectionRaw.includes('unitCostUsd'), 'no debe consumirse unitCostUsd (casing incorrecto de UnitCostUSD)');
  });

  it('6. El escáner dispara UN solo POST /scan (el resolver consume la misma promesa)', () => {
    const scanCallSites = normalizedSource.match(/scanCode\(/g) || [];
    assert.strictEqual(scanCallSites.length, 1, 'scanCode() debe tener un único punto de llamada');
    assert.ok(normalizedSource.includes('onCodeScanned={handleScannedCode}'));
    assert.ok(normalizedSource.includes('resolveProduct={resolveScannedProduct}'));
    assert.ok(normalizedSource.includes('scanRequestRef.current = { code, promise }'));
    assert.ok(normalizedSource.includes('const pending = resolvePendingScanRequest(scanRequestRef.current, code)'));
  });

  it('6b. resolvePendingScanRequest reutiliza la promesa existente (un solo fetch) y valida el código', async () => {
    let fetchCount = 0;
    const promise = Promise.resolve().then(() => {
      fetchCount += 1;
      return { productId: 3, sku: 'ABC', productName: 'Producto' };
    });
    const request = { code: 'ABC', promise };

    const consumed = resolvePendingScanRequest(request, ' ABC ');
    assert.strictEqual(consumed, promise, 'debe devolver la MISMA promesa, no lanzar otra petición');
    await consumed;
    assert.strictEqual(fetchCount, 1);

    assert.strictEqual(resolvePendingScanRequest(request, 'OTRO'), null, 'un código distinto no resuelve el HUD');
    assert.strictEqual(resolvePendingScanRequest(null, 'ABC'), null);
  });

  it('7. Al cambiar de sesión se resetea ítems/review/feedback y se recarga el detalle', () => {
    assert.ok(normalizedSource.includes('setDetail(null); setItems([]); setReview(null);'));
    assert.ok(
      normalizedSource.includes('}, [selectedSessionId, reloadToken]);'),
      'el detalle se recarga al cambiar de sesión o al refrescar tras una mutación'
    );
  });

  it('8. Los controles de gestión Admin/Manager están gateados por isElevated', () => {
    assert.ok(normalizedSource.includes('canManageStockCount(user?.role)'));
    assert.ok(normalizedSource.includes('if (!isElevated || !selectedSessionId) return;'));
    const gated = normalizedSource.match(/isElevated &&/g) || [];
    assert.ok(gated.length >= 3, `esperaba al menos 3 gates isElevated, encontré ${gated.length}`);
  });
});
