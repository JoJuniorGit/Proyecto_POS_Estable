import { describe, it, afterEach } from 'node:test';
import assert from 'node:assert';
import { ApiError, apiFetch } from './api.js';

describe('apiFetch structured ApiError contract', () => {
  let originalFetch;

  function mockErrorResponse(status, rawBody, statusText = 'Bad Request') {
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

  it('1. Attaches status and parsed body while preserving the user-facing message', async () => {
    mockErrorResponse(400, JSON.stringify({ message: 'Saldo insuficiente en la caja' }));

    await assert.rejects(
      () => apiFetch('/api/test'),
      (err) => {
        assert.ok(err instanceof ApiError);
        assert.ok(err instanceof Error);
        assert.strictEqual(err.name, 'ApiError');
        assert.strictEqual(err.status, 400);
        assert.deepStrictEqual(err.body, { message: 'Saldo insuficiente en la caja' });
        assert.strictEqual(err.message, 'Saldo insuficiente en la caja');
        return true;
      }
    );
  });

  it('2. Attaches PascalCase ProblemDetails bodies and conflict statuses', async () => {
    mockErrorResponse(409, JSON.stringify({ Message: 'El turno ya fue cerrado' }));

    await assert.rejects(
      () => apiFetch('/api/shifts/close'),
      (err) => {
        assert.strictEqual(err.status, 409);
        assert.deepStrictEqual(err.body, { Message: 'El turno ya fue cerrado' });
        assert.strictEqual(err.message, 'El turno ya fue cerrado');
        return true;
      }
    );
  });

  it('3. Keeps the raw text body and message for non-JSON error responses', async () => {
    mockErrorResponse(400, 'SKU inválido');

    await assert.rejects(
      () => apiFetch('/api/products'),
      (err) => {
        assert.strictEqual(err.status, 400);
        assert.strictEqual(err.body, 'SKU inválido');
        assert.strictEqual(err.message, 'SKU inválido');
        return true;
      }
    );
  });

  it('4. Preserves the validation errors object in the body', async () => {
    mockErrorResponse(422, JSON.stringify({
      errors: {
        Sku: ['El SKU es requerido'],
        Name: ['El nombre es obligatorio'],
      },
    }));

    await assert.rejects(
      () => apiFetch('/api/products'),
      (err) => {
        assert.strictEqual(err.status, 422);
        assert.deepStrictEqual(err.body, {
          errors: {
            Sku: ['El SKU es requerido'],
            Name: ['El nombre es obligatorio'],
          },
        });
        assert.strictEqual(err.message, 'El SKU es requerido; El nombre es obligatorio');
        return true;
      }
    );
  });

  it('5. Keeps the requiresPasswordChange flag with structured status and body', async () => {
    mockErrorResponse(403, JSON.stringify({ requiresPasswordChange: true, message: 'Debe cambiar su contraseña' }));

    await assert.rejects(
      () => apiFetch('/api/auth/login'),
      (err) => {
        assert.strictEqual(err.requiresPasswordChange, true);
        assert.strictEqual(err.status, 403);
        assert.deepStrictEqual(err.body, { requiresPasswordChange: true, message: 'Debe cambiar su contraseña' });
        assert.strictEqual(err.message, 'Debe cambiar su contraseña');
        return true;
      }
    );
  });

  it('6. Never leaks an HTML body as the user-facing message', async () => {
    mockErrorResponse(400, '<html><body>Proxy error</body></html>');

    await assert.rejects(
      () => apiFetch('/api/test'),
      (err) => {
        assert.strictEqual(err.message, 'Error 400: Bad Request');
        assert.strictEqual(err.body, '<html><body>Proxy error</body></html>');
        return true;
      }
    );
  });

  it('7. Falls back to the status line when the body carries no message', async () => {
    mockErrorResponse(500, '{}', 'Internal Server Error');

    await assert.rejects(
      () => apiFetch('/api/test'),
      (err) => {
        assert.strictEqual(err.status, 500);
        assert.strictEqual(err.message, 'Error 500: Internal Server Error');
        assert.deepStrictEqual(err.body, {});
        return true;
      }
    );
  });
});
