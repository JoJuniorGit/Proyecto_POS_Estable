import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { getProductSuggestions } from '../../services/productsApi.js';

/**
 * 8.179-T3 (S4 Web): la búsqueda del POS acepta una categoría opcional. PosPage carga el
 * listado de categorías y lo pasa al SearchBar; las sugerencias viajan a
 * GET /api/products/suggestions?...&categoryId=<id> sólo cuando hay selección.
 */
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const searchBarSource = fs
  .readFileSync(path.resolve(__dirname, 'SearchBar.jsx'), 'utf-8')
  .replace(/\s+/g, ' ');
const posPageSource = fs
  .readFileSync(path.resolve(__dirname, '../../pages/PosPage.jsx'), 'utf-8')
  .replace(/\s+/g, ' ');

function jsonResponse(payload) {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    headers: { get: () => 'application/json' },
    text: async () => JSON.stringify(payload),
    json: async () => payload,
  };
}

describe('POS search — categoría en sugerencias (8.179-T3, S4)', () => {
  let originalFetch;
  let fetchCalls;

  beforeEach(() => {
    originalFetch = global.fetch;
    fetchCalls = [];
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return jsonResponse([]);
    };
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  it('1. Con categoryId la URL de sugerencias filtra por categoría', async () => {
    await getProductSuggestions('leche', undefined, 5);

    assert.strictEqual(fetchCalls.length, 1);
    assert.ok(
      fetchCalls[0].url.endsWith('/api/products/suggestions?filter=leche&categoryId=5'),
      fetchCalls[0].url
    );
  });

  it('2. Sin categoryId ("Todas") la URL no lleva el parámetro', async () => {
    await getProductSuggestions('leche', undefined);

    assert.strictEqual(fetchCalls.length, 1);
    assert.ok(!fetchCalls[0].url.includes('categoryId'), fetchCalls[0].url);
    assert.ok(fetchCalls[0].url.endsWith('/api/products/suggestions?filter=leche'), fetchCalls[0].url);
  });

  it('3. Valores vacíos/null no agregan el parámetro (contrato opcional)', async () => {
    await getProductSuggestions('leche', undefined, '');
    await getProductSuggestions('leche', undefined, null);

    assert.strictEqual(fetchCalls.length, 2);
    for (const call of fetchCalls) {
      assert.ok(!call.url.includes('categoryId'), call.url);
    }
  });

  it('3b. Con filtro vacío no hay petición aunque haya categoría seleccionada', async () => {
    assert.deepStrictEqual(await getProductSuggestions('   ', undefined, 5), []);
    assert.strictEqual(fetchCalls.length, 0);
  });

  it('4. SearchBar reenvía categoryId a las sugerencias y refetchea al cambiar la categoría', () => {
    assert.ok(
      searchBarSource.includes('getProductSuggestions(debouncedQuery, controller.signal, categoryId)'),
      'las sugerencias deben viajar con la categoría activa'
    );
    assert.ok(
      searchBarSource.includes('}, [debouncedQuery, categoryId]);'),
      'categoryId debe estar en las dependencias del efecto de búsqueda'
    );
  });

  it('5. PosPage carga las categorías y las pasa al SearchBar', () => {
    assert.ok(posPageSource.includes('getCategories('), 'PosPage debe cargar las categorías');
    assert.ok(
      posPageSource.includes(
        '<SearchBar ref={searchBarRef} onSelectProduct={handleSelectProduct} categoryId={categoryId} />'
      ),
      'PosPage debe pasar la categoría seleccionada al SearchBar'
    );
  });
});
