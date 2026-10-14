import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert';
import {
  listStockCounts,
  createStockCount,
  getStockCount,
  scanCode,
  setItemQuantity,
  removeItem,
  getReview,
  closeStockCount,
  applyStockCount,
  cancelStockCount,
} from './stockCountApi.js';

function jsonResponse(payload, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 200 ? 'OK' : 'Error',
    headers: { get: () => 'application/json' },
    text: async () => JSON.stringify(payload),
    json: async () => payload,
  };
}

describe('stockCountApi — contrato /api/stock-counts (8.180-T2, S5)', () => {
  let originalFetch;
  let fetchCalls;

  beforeEach(() => {
    originalFetch = global.fetch;
    fetchCalls = [];
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  function mockFetch(payload = {}, status = 200) {
    global.fetch = async (url, config) => {
      fetchCalls.push({ url: String(url), config });
      return jsonResponse(payload, status);
    };
  }

  it('1. listStockCounts sin filtro hace GET /api/stock-counts sin query', async () => {
    mockFetch([]);
    const result = await listStockCounts();

    assert.strictEqual(fetchCalls.length, 1);
    assert.ok(fetchCalls[0].url.endsWith('/api/stock-counts'), fetchCalls[0].url);
    assert.ok(!fetchCalls[0].url.includes('?'), 'sin filtro no debe viajar query');
    assert.strictEqual(fetchCalls[0].config.method, 'GET');
    assert.deepStrictEqual(result, []);
  });

  it('2. listStockCounts(status) filtra por nombre en query (?status=Open)', async () => {
    mockFetch([]);
    await listStockCounts('Open');

    assert.ok(fetchCalls[0].url.endsWith('/api/stock-counts?status=Open'), fetchCalls[0].url);
  });

  it('3. listStockCounts degrada a [] cuando el backend no devuelve una lista', async () => {
    mockFetch({ items: [] });
    assert.deepStrictEqual(await listStockCounts(), []);
  });

  it('4. createStockCount normaliza scopeCategoryId/notes a null cuando no vienen', async () => {
    mockFetch({ id: 1 });
    await createStockCount({ name: 'Conteo octubre', scopeCategoryId: 5 });
    assert.strictEqual(fetchCalls[0].config.method, 'POST');
    assert.ok(fetchCalls[0].url.endsWith('/api/stock-counts'));
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), {
      name: 'Conteo octubre',
      scopeCategoryId: 5,
      notes: null,
    });

    await createStockCount({ name: 'Todo el inventario' });
    assert.deepStrictEqual(JSON.parse(fetchCalls[1].config.body), {
      name: 'Todo el inventario',
      scopeCategoryId: null,
      notes: null,
    });
  });

  it('5. getStockCount hace GET del detalle ciego de la sesión', async () => {
    mockFetch({ session: { id: 7 }, items: [] });
    const detail = await getStockCount(7);

    assert.ok(fetchCalls[0].url.endsWith('/api/stock-counts/7'), fetchCalls[0].url);
    assert.strictEqual(fetchCalls[0].config.method, 'GET');
    assert.deepStrictEqual(detail, { session: { id: 7 }, items: [] });
  });

  it('6. scanCode hace POST {code, quantityDelta} con delta default 1 y código trimmeado', async () => {
    mockFetch({ productId: 3 });
    await scanCode(7, ' 7591234567890 ');
    assert.strictEqual(fetchCalls[0].config.method, 'POST');
    assert.ok(fetchCalls[0].url.endsWith('/api/stock-counts/7/scan'), fetchCalls[0].url);
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), {
      code: '7591234567890',
      quantityDelta: 1,
    });

    await scanCode(7, 'ABC-1', -1);
    assert.deepStrictEqual(JSON.parse(fetchCalls[1].config.body), {
      code: 'ABC-1',
      quantityDelta: -1,
    });
  });

  it('7. setItemQuantity corrige la cantidad contada con PUT absoluto', async () => {
    mockFetch({ productId: 3, countedQuantity: 4.5 });
    await setItemQuantity(7, 3, 4.5);

    assert.strictEqual(fetchCalls[0].config.method, 'PUT');
    assert.ok(fetchCalls[0].url.endsWith('/api/stock-counts/7/items/3'), fetchCalls[0].url);
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), { countedQuantity: 4.5 });
  });

  it('8. removeItem quita el escaneo erróneo con DELETE', async () => {
    mockFetch({ message: 'ok' });
    await removeItem(7, 3);

    assert.strictEqual(fetchCalls[0].config.method, 'DELETE');
    assert.ok(fetchCalls[0].url.endsWith('/api/stock-counts/7/items/3'), fetchCalls[0].url);
  });

  it('9. getReview hace GET del descuadre (solo Admin/Manager en backend)', async () => {
    mockFetch({ sessionId: 7, items: [] });
    const review = await getReview(7);

    assert.strictEqual(fetchCalls[0].config.method, 'GET');
    assert.ok(fetchCalls[0].url.endsWith('/api/stock-counts/7/review'), fetchCalls[0].url);
    assert.strictEqual(review.sessionId, 7);
  });

  it('10. close/apply/cancel hacen POST sin body a sus rutas dedicadas', async () => {
    const actions = [
      [closeStockCount, 'close'],
      [applyStockCount, 'apply'],
      [cancelStockCount, 'cancel'],
    ];

    for (const [fn, route] of actions) {
      fetchCalls = [];
      mockFetch({ id: 7 });
      await fn(7);
      assert.strictEqual(fetchCalls[0].config.method, 'POST', route);
      assert.ok(fetchCalls[0].url.endsWith(`/api/stock-counts/7/${route}`), fetchCalls[0].url);
      assert.strictEqual(fetchCalls[0].config.body, undefined, `${route} no lleva body`);
    }
  });

  it('11. propaga el ProblemDetails del backend como ApiError legible', async () => {
    mockFetch({ message: 'La sesión de conteo no está abierta.', title: 'Conflict' }, 409);
    await assert.rejects(
      () => scanCode(7, 'ABC'),
      (err) => err.status === 409 && err.message === 'La sesión de conteo no está abierta.',
      'el 409 de guard de estado debe llegar con el mensaje del backend'
    );

    fetchCalls = [];
    mockFetch({ message: "Código no encontrado: 'ZZZ'. Verifique el SKU o código de barras." }, 404);
    await assert.rejects(
      () => scanCode(7, 'ZZZ'),
      (err) => err.status === 404 && /Código no encontrado/.test(err.message)
    );
  });
});
