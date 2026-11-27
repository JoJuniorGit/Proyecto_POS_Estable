import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { resolveSuggestionFetchMode } from './SearchBar.jsx';

/**
 * 8.186-T7 (S1, refactor H3): el buscador del POS debe tener UN ÚNICO camino de fetch de
 * sugerencias. `resolveSuggestionFetchMode` decide, para cada commit del efecto único
 * ([exchangeRate, debouncedQuery, categoryId]), una sola acción: 'fetch' cuando cambia la
 * consulta/categoría o la tasa real; 'skip' en la transición inicial 0 → tasa y en repeticiones
 * (shouldRefetchOnRateChange); 'clear' sin consulta activa. Tasa y consulta en el mismo commit
 * producen un único 'fetch' (los dos efectos de 8.186-T2 duplicaban la petición).
 * node:test corre sin DOM: la cobertura combina la decisión pura y pines del cableado del efecto
 * único (un solo call site de sugerencias, aborto al superseder y al desmontar).
 */
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'SearchBar.jsx'), 'utf-8');
const normalizedSource = source.replace(/\s+/g, ' ');

describe('SearchBar — un único fetch de sugerencias (8.186-T7, S1, H3)', () => {
  it('1. Cambio de consulta → una petición', () => {
    assert.strictEqual(
      resolveSuggestionFetchMode(
        { rate: 50, query: '', categoryId: '' },
        { rate: 50, query: 'leche', categoryId: '' }
      ),
      'fetch'
    );
    assert.strictEqual(
      resolveSuggestionFetchMode(
        { rate: 50, query: 'leche', categoryId: '' },
        { rate: 50, query: 'leche entera', categoryId: '' }
      ),
      'fetch'
    );
  });

  it('2. Cambio de categoría con la misma consulta → una petición', () => {
    assert.strictEqual(
      resolveSuggestionFetchMode(
        { rate: 50, query: 'leche', categoryId: '' },
        { rate: 50, query: 'leche', categoryId: 5 }
      ),
      'fetch'
    );
    assert.strictEqual(
      resolveSuggestionFetchMode(
        { rate: 50, query: 'leche', categoryId: 5 },
        { rate: 50, query: 'leche', categoryId: 7 }
      ),
      'fetch'
    );
  });

  it('3. Cambio real de tasa con la consulta activa → refetch (reproyección del servidor)', () => {
    assert.strictEqual(
      resolveSuggestionFetchMode(
        { rate: 50, query: 'leche', categoryId: '' },
        { rate: 60, query: 'leche', categoryId: '' }
      ),
      'fetch'
    );
    assert.strictEqual(
      resolveSuggestionFetchMode(
        { rate: 60, query: 'leche', categoryId: 5 },
        { rate: 50, query: 'leche', categoryId: 5 }
      ),
      'fetch'
    );
  });

  it('4. Tasa y consulta en el MISMO commit → una sola decisión de fetch y un solo call site', () => {
    const mode = resolveSuggestionFetchMode(
      { rate: 50, query: '', categoryId: '' },
      { rate: 60, query: 'leche', categoryId: '' }
    );
    assert.strictEqual(mode, 'fetch');

    const callSites = source.match(/getProductSuggestions\(/g) || [];
    assert.strictEqual(
      callSites.length,
      1,
      'debe existir UN solo call site de sugerencias (sin efecto duplicado H3)'
    );
  });

  it('5. Transición inicial 0 → tasa con consulta activa → skip (sin re-trigger de montaje)', () => {
    assert.strictEqual(
      resolveSuggestionFetchMode(
        { rate: 0, query: 'leche', categoryId: '' },
        { rate: 36.5, query: 'leche', categoryId: '' }
      ),
      'skip'
    );
  });

  it('6. Tasa repetida → skip (el mismo disparo no refetchea dos veces; StrictMode-safe)', () => {
    assert.strictEqual(
      resolveSuggestionFetchMode(
        { rate: 36.5, query: 'leche', categoryId: '' },
        { rate: 36.5, query: 'leche', categoryId: '' }
      ),
      'skip'
    );
    assert.strictEqual(
      resolveSuggestionFetchMode(
        { rate: 36.5, query: 'leche', categoryId: 5 },
        { rate: 36.5, query: 'leche', categoryId: 5 }
      ),
      'skip'
    );
  });

  it('7. Tasa → 0 con consulta activa → skip (semántica shouldRefetchOnRateChange intacta)', () => {
    assert.strictEqual(
      resolveSuggestionFetchMode(
        { rate: 36.5, query: 'leche', categoryId: '' },
        { rate: 0, query: 'leche', categoryId: '' }
      ),
      'skip'
    );
  });

  it('8. Consulta vacía o sólo espacios → clear (sin búsqueda activa, sin petición)', () => {
    assert.strictEqual(
      resolveSuggestionFetchMode(
        { rate: 50, query: 'leche', categoryId: '' },
        { rate: 50, query: '', categoryId: '' }
      ),
      'clear'
    );
    assert.strictEqual(
      resolveSuggestionFetchMode(
        { rate: 50, query: 'leche', categoryId: '' },
        { rate: 50, query: '   ', categoryId: '' }
      ),
      'clear'
    );
    assert.strictEqual(
      resolveSuggestionFetchMode(
        { rate: 0, query: '', categoryId: '' },
        { rate: 0, query: '', categoryId: '' }
      ),
      'clear'
    );
  });

  it('9. El efecto único decide, usa las deps fusionadas y aborta al superseder y al desmontar', () => {
    assert.ok(
      normalizedSource.includes('const mode = resolveSuggestionFetchMode(previousTrigger, nextTrigger);'),
      'el efecto debe decidir con resolveSuggestionFetchMode'
    );
    assert.ok(
      normalizedSource.includes('}, [exchangeRate, debouncedQuery, categoryId]);'),
      'deps fusionadas del efecto único'
    );
    assert.ok(
      normalizedSource.includes('fetchControllerRef.current?.abort();'),
      'cada nuevo disparo (fetch o clear) aborta la petición en vuelo'
    );
    assert.ok(
      normalizedSource.includes('useEffect(() => () => { fetchControllerRef.current?.abort();'),
      'el desmontaje aborta la última petición en vuelo'
    );
    assert.ok(
      !normalizedSource.includes('rateFetchRef'),
      'no debe quedar el efecto dedicado de tasa (fetch duplicado H3)'
    );
  });
});
