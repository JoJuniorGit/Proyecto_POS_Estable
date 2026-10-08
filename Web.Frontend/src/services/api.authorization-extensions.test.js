import { describe, it, afterEach } from 'node:test';
import assert from 'node:assert';
import { ApiError, apiFetch } from './api.js';

// 8.150 (T8, design D7): el 403 del gate de acciones protegidas viaja con extensiones
// ProblemDetails (authorizationRequired / authorizationAction); el cliente web debe exponerlas
// sin alterar el manejo existente de 401/403 y demas errores.
describe('apiFetch authorization flow extensions', () => {
  let originalFetch;

  function mockErrorResponse(status, rawBody, statusText = 'Forbidden') {
    originalFetch = global.fetch;
    global.fetch = async () => ({
      ok: false,
      status,
      statusText,
      headers: { get: () => 'application/json' },
      text: async () => rawBody,
      json: async () => JSON.parse(rawBody),
    });
  }

  afterEach(() => {
    if (originalFetch) {
      global.fetch = originalFetch;
      originalFetch = undefined;
    }
  });

  it('1. surfaces authorizationRequired/authorizationAction from a 403 ProblemDetails body', async () => {
    mockErrorResponse(403, JSON.stringify({
      title: 'Forbidden',
      detail: 'Modificación de precios no autorizada. Se requiere rol de Administrador o Supervisor.',
      message: 'Modificación de precios no autorizada. Se requiere rol de Administrador o Supervisor.',
      authorizationRequired: true,
      authorizationAction: 'ManualPriceOverride',
    }));

    await assert.rejects(
      () => apiFetch('/api/sales/1/items'),
      (err) => {
        assert.ok(err instanceof ApiError);
        assert.strictEqual(err.status, 403);
        assert.strictEqual(err.authorizationRequired, true);
        assert.strictEqual(err.authorizationAction, 'ManualPriceOverride');
        assert.strictEqual(
          err.message,
          'Modificación de precios no autorizada. Se requiere rol de Administrador o Supervisor.'
        );
        assert.strictEqual(err.body.authorizationRequired, true);
        return true;
      }
    );
  });

  it('2. defaults the extension fields when the error body has none and keeps existing handling', async () => {
    mockErrorResponse(403, JSON.stringify({ message: 'No tiene permisos para realizar esta operación.' }));

    await assert.rejects(
      () => apiFetch('/api/products'),
      (err) => {
        assert.strictEqual(err.status, 403);
        assert.strictEqual(err.message, 'No tiene permisos para realizar esta operación.');
        assert.strictEqual(err.authorizationRequired, false);
        assert.strictEqual(err.authorizationAction, null);
        assert.deepStrictEqual(err.body, { message: 'No tiene permisos para realizar esta operación.' });
        return true;
      }
    );
  });

  it('3. defaults the extension fields for non-JSON error bodies', async () => {
    mockErrorResponse(400, 'SKU inválido', 'Bad Request');

    await assert.rejects(
      () => apiFetch('/api/products'),
      (err) => {
        assert.strictEqual(err.status, 400);
        assert.strictEqual(err.message, 'SKU inválido');
        assert.strictEqual(err.authorizationRequired, false);
        assert.strictEqual(err.authorizationAction, null);
        return true;
      }
    );
  });

  it('4. exposes the fields on directly constructed ApiError instances', () => {
    const withExtensions = new ApiError('gate', 403, {
      authorizationRequired: true,
      authorizationAction: 'ManualPriceOverride',
    });
    assert.strictEqual(withExtensions.authorizationRequired, true);
    assert.strictEqual(withExtensions.authorizationAction, 'ManualPriceOverride');

    const withoutExtensions = new ApiError('boom', 500, 'text');
    assert.strictEqual(withoutExtensions.authorizationRequired, false);
    assert.strictEqual(withoutExtensions.authorizationAction, null);
  });
});
