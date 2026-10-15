import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert';
import { getCategories, toCategoryOptions } from './categoriesApi.js';

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

describe('categoriesApi — contrato GET /api/categories (8.179-T3, S4)', () => {
  let originalFetch;
  let fetchCalls;

  beforeEach(() => {
    originalFetch = global.fetch;
    fetchCalls = [];
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  it('1. Hace GET /api/categories y devuelve el listado plano del backend', async () => {
    const payload = [
      { id: 1, name: 'Alimentos', parentCategoryId: null, depth: 0, path: 'Alimentos' },
      { id: 2, name: 'Lácteos', parentCategoryId: 1, depth: 1, path: 'Alimentos > Lácteos' },
    ];
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return jsonResponse(payload);
    };

    const result = await getCategories();

    assert.strictEqual(fetchCalls.length, 1, 'debe ser una única petición');
    assert.ok(fetchCalls[0].url.endsWith('/api/categories'), fetchCalls[0].url);
    assert.strictEqual(fetchCalls[0].config.method, 'GET');
    assert.deepStrictEqual(result, payload);
  });

  it('2. Devuelve [] cuando el backend responde algo que no es una lista (no bloquea la UI)', async () => {
    global.fetch = async () => jsonResponse({ items: [] });
    assert.deepStrictEqual(await getCategories(), []);
  });

  it('3. Propaga el error del backend como ApiError con status', async () => {
    global.fetch = async () => jsonResponse({ message: 'Sin permisos' }, 403);
    await assert.rejects(
      () => getCategories(),
      (err) => err.status === 403,
      'un 403 debe propagarse para que la página lo degrade sin bloquear'
    );
  });

  it('4. toCategoryOptions indenta por depth y conserva la ruta completa para desambiguar', () => {
    const options = toCategoryOptions([
      { id: 1, name: 'Alimentos', parentCategoryId: null, depth: 0, path: 'Alimentos' },
      { id: 2, name: 'Lácteos', parentCategoryId: 1, depth: 1, path: 'Alimentos > Lácteos' },
      { id: 3, name: 'Productos > Bebidas', parentCategoryId: null, depth: 0, path: 'Productos > Bebidas' },
    ]);

    assert.deepStrictEqual(options, [
      { value: '1', label: 'Alimentos', path: 'Alimentos' },
      { value: '2', label: '\u00A0\u00A0Lácteos', path: 'Alimentos > Lácteos' },
      { value: '3', label: 'Productos > Bebidas', path: 'Productos > Bebidas' },
    ]);
  });

  it('5. toCategoryOptions tolera listas vacías o inválidas', () => {
    assert.deepStrictEqual(toCategoryOptions([]), []);
    assert.deepStrictEqual(toCategoryOptions(null), []);
    assert.deepStrictEqual(toCategoryOptions(undefined), []);
  });
});
