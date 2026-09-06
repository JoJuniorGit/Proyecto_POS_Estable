import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert';
import { resolveBaseUrl, setCustomBaseUrl, apiFetch } from './api.js';

describe('api.js resolveBaseUrl & setCustomBaseUrl', () => {
  let originalWindow;
  let originalLocalStorage;
  let mockStorage = {};

  beforeEach(() => {
    mockStorage = {};
    originalWindow = global.window;
    originalLocalStorage = global.localStorage;

    global.localStorage = {
      getItem: (key) => mockStorage[key] || null,
      setItem: (key, val) => { mockStorage[key] = String(val); },
      removeItem: (key) => { delete mockStorage[key]; },
      clear: () => { mockStorage = {}; }
    };
  });

  afterEach(() => {
    global.window = originalWindow;
    global.localStorage = originalLocalStorage;
  });

  it('1. Returns http://localhost:5000 when window is undefined', () => {
    global.window = undefined;
    const url = resolveBaseUrl();
    assert.strictEqual(url, 'http://localhost:5000');
  });

  it('2. On Kestrel HTTPS port 5001 returns https origin', () => {
    global.window = {
      location: {
        protocol: 'https:',
        origin: 'https://192.168.1.50:5001',
        hostname: '192.168.1.50',
        port: '5001',
        search: ''
      }
    };

    const url = resolveBaseUrl();
    assert.strictEqual(url, 'https://192.168.1.50:5001');
  });

  it('3. On Kestrel HTTP port 5000 returns http origin', () => {
    global.window = {
      location: {
        protocol: 'http:',
        origin: 'http://192.168.1.50:5000',
        hostname: '192.168.1.50',
        port: '5000',
        search: ''
      }
    };

    const url = resolveBaseUrl();
    assert.strictEqual(url, 'http://192.168.1.50:5000');
  });

  it('4. When page is HTTPS and localStorage has http://192.168.1.10:5000, sanitizes to https://192.168.1.10:5001 and updates localStorage', () => {
    mockStorage['pos_custom_api_url'] = 'http://192.168.1.10:5000';
    global.window = {
      location: {
        protocol: 'https:',
        origin: 'https://192.168.1.50:5173',
        hostname: '192.168.1.50',
        port: '5173',
        search: ''
      }
    };

    const url = resolveBaseUrl();
    assert.strictEqual(url, 'https://192.168.1.10:5001');
    assert.strictEqual(mockStorage['pos_custom_api_url'], 'https://192.168.1.10:5001');
  });

  it('5. When page is HTTP and localStorage has http://192.168.1.10:5000, preserves stored value', () => {
    mockStorage['pos_custom_api_url'] = 'http://192.168.1.10:5000';
    global.window = {
      location: {
        protocol: 'http:',
        origin: 'http://192.168.1.50:5173',
        hostname: '192.168.1.50',
        port: '5173',
        search: ''
      }
    };

    const url = resolveBaseUrl();
    assert.strictEqual(url, 'http://192.168.1.10:5000');
  });

  it('6. With ?server=192.168.1.100:5000 on HTTPS page, normalizes and maps to https://192.168.1.100:5001', () => {
    global.window = {
      location: {
        protocol: 'https:',
        origin: 'https://192.168.1.100:5173',
        hostname: '192.168.1.100',
        port: '5173',
        search: '?server=192.168.1.100:5000',
        pathname: '/'
      },
      history: {
        replaceState: () => {}
      }
    };

    const url = resolveBaseUrl();
    assert.strictEqual(url, 'https://192.168.1.100:5001');
    assert.strictEqual(mockStorage['pos_custom_api_url'], 'https://192.168.1.100:5001');
  });

  it('7. setCustomBaseUrl sanitizes http to https on HTTPS page for a permitted LAN host', () => {
    global.window = {
      location: {
        protocol: 'https:',
        origin: 'https://localhost:5001',
        hostname: 'localhost',
        port: '5001'
      }
    };

    setCustomBaseUrl('http://192.168.1.20:5000');
    assert.strictEqual(mockStorage['pos_custom_api_url'], 'https://192.168.1.20:5001');
  });

  it('8. setCustomBaseUrl rejects a remote (non-private, non-loopback) host', () => {
    global.window = {
      location: {
        protocol: 'https:',
        origin: 'https://localhost:5001',
        hostname: 'localhost',
        port: '5001'
      }
    };

    setCustomBaseUrl('https://api.evil.com');
    // No debe persistirse ni actualizar la URL base
    assert.strictEqual(mockStorage['pos_custom_api_url'], undefined);
  });

  it('9. setCustomBaseUrl rejects a public-IP LAN-looking host', () => {
    global.window = {
      location: {
        protocol: 'https:',
        origin: 'https://localhost:5001',
        hostname: 'localhost',
        port: '5001'
      }
    };

    setCustomBaseUrl('https://8.8.8.8');
    assert.strictEqual(mockStorage['pos_custom_api_url'], undefined);
  });
});

describe('apiFetch ProblemDetails and validation error extraction', () => {
  let originalFetch;
  let originalLocalStorage;

  beforeEach(() => {
    originalLocalStorage = global.localStorage;
    global.localStorage = {
      getItem: () => null,
      setItem: () => {},
      removeItem: () => {},
      clear: () => {}
    };
  });

  afterEach(() => {
    if (originalFetch) global.fetch = originalFetch;
    global.localStorage = originalLocalStorage;
  });

  function mockFetchResponse(status, bodyJson, statusText = 'Bad Request') {
    originalFetch = global.fetch;
    global.fetch = async () => ({
      ok: status >= 200 && status < 300,
      status,
      statusText,
      headers: {
        get: (h) => (h.toLowerCase() === 'content-type' ? 'application/json' : null)
      },
      text: async () => JSON.stringify(bodyJson),
      json: async () => bodyJson
    });
  }

  it('1. Extracts RFC 7807 detail', async () => {
    mockFetchResponse(400, { detail: 'Saldo insuficiente en la caja' });
    await assert.rejects(
      async () => await apiFetch('/api/test'),
      { message: 'Saldo insuficiente en la caja' }
    );
  });

  it('2. Extracts RFC 7807 title when detail is absent', async () => {
    mockFetchResponse(400, { title: 'One or more validation errors occurred.' });
    await assert.rejects(
      async () => await apiFetch('/api/test'),
      { message: 'One or more validation errors occurred.' }
    );
  });

  it('3. Extracts validation errors from ASP.NET Core object format', async () => {
    mockFetchResponse(400, {
      errors: {
        Sku: ['El SKU es requerido'],
        Name: ['El nombre es obligatorio']
      }
    });
    await assert.rejects(
      async () => await apiFetch('/api/test'),
      { message: 'El SKU es requerido; El nombre es obligatorio' }
    );
  });

  it('4. Extracts validation errors from array format', async () => {
    mockFetchResponse(400, {
      errors: ['Error A', 'Error B']
    });
    await assert.rejects(
      async () => await apiFetch('/api/test'),
      { message: 'Error A; Error B' }
    );
  });

  it('5. Prioritizes standard message property', async () => {
    mockFetchResponse(400, { message: 'Operación no permitida para el usuario' });
    await assert.rejects(
      async () => await apiFetch('/api/test'),
      { message: 'Operación no permitida para el usuario' }
    );
  });

  it('6. Preserves Content-Type application/json when options.headers are provided with body', async () => {
    let capturedConfig = null;
    originalFetch = global.fetch;
    global.fetch = async (url, config) => {
      capturedConfig = config;
      return {
        ok: true,
        status: 200,
        headers: { get: () => 'application/json' },
        text: async () => JSON.stringify({ success: true }),
        json: async () => ({ success: true })
      };
    };

    await apiFetch('/api/sales/1/complete', {
      method: 'POST',
      body: JSON.stringify({ exchangeRate: 40 }),
      headers: { 'Idempotency-Key': 'test-uuid-123' }
    });

    assert.ok(capturedConfig, 'fetch should have been called');
    assert.strictEqual(capturedConfig.headers['Content-Type'], 'application/json');
    assert.strictEqual(capturedConfig.headers['Accept'], 'application/json');
    assert.strictEqual(capturedConfig.headers['Idempotency-Key'], 'test-uuid-123');
    assert.strictEqual(capturedConfig.headers['X-Client-Platform'], 'Web');
  });
});

