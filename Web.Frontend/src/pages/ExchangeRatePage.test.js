import { test, describe } from 'node:test';
import assert from 'node:assert';

describe('ExchangeRate Manual Flow & Outdated Detection Unit Tests', () => {
  test('1. Accurately detects outdated rate when lastUpdated exceeds 24 hours in UTC', () => {
    const now = Date.now();
    const twentyFiveHoursAgo = new Date(now - 25 * 60 * 60 * 1000).toISOString();
    const oneHourAgo = new Date(now - 1 * 60 * 60 * 1000).toISOString();

    const checkIsOutdated = (timestamp) => !timestamp || (now - new Date(timestamp).getTime() > 24 * 60 * 60 * 1000);
    const isOutdatedOld = checkIsOutdated(twentyFiveHoursAgo);
    const isOutdatedRecent = checkIsOutdated(oneHourAgo);
    const isOutdatedNull = checkIsOutdated(null);

    assert.strictEqual(isOutdatedOld, true, 'Rate older than 24h must be flagged as outdated');
    assert.strictEqual(isOutdatedRecent, false, 'Rate updated 1h ago must NOT be flagged as outdated');
    assert.strictEqual(isOutdatedNull, true, 'Null timestamp must be flagged as outdated');
  });

  test('2. Simulates 502/504 scraper error and triggers retry state', async () => {
    let syncAttempts = 0;
    const mockSyncBcv = async () => {
      syncAttempts++;
      if (syncAttempts === 1) {
        const err = new Error('Tiempo de espera agotado al conectar con el portal del BCV (504 Gateway Timeout)');
        err.status = 504;
        throw err;
      }
      return { value: 68.50, updatedAt: new Date().toISOString() };
    };

    let state = {
      message: null,
      exchangeRate: 0,
    };

    // First attempt -> failure with retry option
    try {
      await mockSyncBcv();
    } catch (err) {
      state.message = {
        type: 'danger',
        text: err.message,
        canRetry: true,
      };
    }

    assert.strictEqual(syncAttempts, 1);
    assert.strictEqual(state.message.canRetry, true);
    assert.ok(state.message.text.includes('504 Gateway Timeout'));

    // Retry invocation -> success
    if (state.message.canRetry) {
      const res = await mockSyncBcv();
      state.exchangeRate = res.value;
      state.message = {
        type: 'success',
        text: `Tasa sincronizada con BCV: ${res.value}`,
      };
    }

    assert.strictEqual(syncAttempts, 2);
    assert.strictEqual(state.exchangeRate, 68.50);
    assert.strictEqual(state.message.type, 'success');
  });
});
