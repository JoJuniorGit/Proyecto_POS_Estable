import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * 8.186-T2/T7 (S1/S3, web): la búsqueda del POS relanza la consulta activa de sugerencias cuando
 * la tasa vigente cambia (SignalR) para que el servidor reproyecte el efectivo. Tras el refactor
 * T7 (H3) existe UN único efecto con deps [exchangeRate, debouncedQuery, categoryId]: la transición
 * inicial 0 → tasa NO refetchea (el fetch ya usó la proyección del servidor) y no hay un segundo
 * efecto de tasa que duplique la petición cuando tasa y consulta cambian en el mismo commit.
 */
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'SearchBar.jsx'), 'utf-8');
const normalizedSource = source.replace(/\s+/g, ' ');

describe('SearchBar — refetch de sugerencias al cambiar la tasa (8.186-T2, S1; T7 efecto único)', () => {
  it('1. El cambio de tasa relanza la búsqueda activa dentro del efecto único, abortable', () => {
    assert.ok(
      normalizedSource.includes('shouldRefetchOnRateChange(previous.rate, next.rate)'),
      'el guard debe decidir el refetch por cambio de tasa'
    );
    assert.ok(
      normalizedSource.includes('getProductSuggestions(debouncedQuery, controller.signal, categoryId)'),
      'el refetch de tasa debe volver a consultar la búsqueda activa'
    );
    assert.ok(
      normalizedSource.includes("if (mode === 'clear') {"),
      'sin búsqueda activa no debe dispararse ninguna petición'
    );
    assert.ok(
      normalizedSource.includes('fetchControllerRef.current?.abort();'),
      'el refetch debe abortar el anterior'
    );
  });

  it('2. Un único efecto con deps [exchangeRate, debouncedQuery, categoryId] (sin efecto dedicado)', () => {
    assert.ok(
      normalizedSource.includes('}, [exchangeRate, debouncedQuery, categoryId]);'),
      'las deps del efecto único deben incluir tasa, consulta y categoría'
    );
    assert.ok(
      !normalizedSource.includes('rateFetchRef'),
      'el efecto dedicado de tasa debe desaparecer (elimina el fetch duplicado H3)'
    );
    assert.ok(
      !normalizedSource.includes('}, [debouncedQuery, categoryId]);'),
      'no debe quedar el efecto principal anterior: duplicaría el fetch en el mismo commit'
    );
    assert.ok(
      !normalizedSource.includes('rateReloadToken'),
      'no debe agregarse un token al efecto (evita doble fetch de montaje)'
    );
  });

  it('3. La tasa sigue disponible para el fallback documentado de SuggestionList', () => {
    assert.ok(normalizedSource.includes('exchangeRate={exchangeRate}'), normalizedSource);
  });

  it('4. No implementa matemática de política de redondeo (S3)', () => {
    assert.ok(!source.includes('Math.ceil'), 'sin techo local en JS');
    assert.ok(!source.includes('Math.round'), 'sin redondeo de política local en JS');
  });
});
