import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert';
import { resolveBaseUrl, setCustomBaseUrl } from './api.js';

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
        origin: 'https://192.168.1.50:5173',
        hostname: '192.168.1.50',
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

  it('7. setCustomBaseUrl sanitizes http to https on HTTPS page', () => {
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
});
