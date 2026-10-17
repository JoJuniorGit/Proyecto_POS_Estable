import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * 8.186-T2 (S1/S3, web): la búsqueda del POS relanza la consulta activa de sugerencias cuando la
 * tasa vigente cambia (SignalR) para que el servidor reproyecte el efectivo. La transición inicial
 * 0 → tasa NO refetchea: el fetch de montaje ya usó la tasa del servidor.
 */
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'SearchBar.jsx'), 'utf-8');
const normalizedSource = source.replace(/\s+/g, ' ');

describe('SearchBar — refetch de sugerencias al cambiar la tasa (8.186-T2, S1)', () => {
  it('1. El cambio de tasa relanza la búsqueda activa con un fetch dedicado y abortable', () => {
    assert.ok(
      normalizedSource.includes('shouldRefetchOnRateChange(previousRate, exchangeRate)'),
      'el guard debe decidir el refetch por cambio de tasa'
    );
    assert.ok(
      normalizedSource.includes('getProductSuggestions(queryText, controller.signal, categoryId)'),
      'el refetch de tasa debe volver a consultar la búsqueda activa'
    );
    assert.ok(
      normalizedSource.includes('if (!queryText.trim()) return;'),
      'sin búsqueda activa no debe dispararse ninguna petición'
    );
    assert.ok(
      normalizedSource.includes('rateFetchRef.current?.controller.abort()'),
      'el refetch debe abortar el anterior y limpiarse al desmontar'
    );
  });

  it('2. El efecto principal conserva sus deps (sin token extra ni exchangeRate directo)', () => {
    assert.ok(
      normalizedSource.includes('}, [debouncedQuery, categoryId]);'),
      'las deps del efecto principal no deben cambiar (pin 8.179-T3)'
    );
    assert.ok(
      !normalizedSource.includes('rateReloadToken'),
      'no debe agregarse un token al efecto principal (evita doble fetch de montaje)'
    );
    assert.ok(
      !normalizedSource.includes('debouncedQuery, categoryId, exchangeRate]'),
      'exchangeRate no debe ir directo en deps del efecto principal'
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
