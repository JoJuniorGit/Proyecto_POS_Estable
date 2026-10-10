import { describe, it } from 'node:test';
import assert from 'node:assert';
import {
  AUTHORIZATION_PHASES,
  AUTHORIZATION_STRINGS,
  applyAuthorizationExpiry,
  applyAuthorizationResolution,
  cancelAuthorizationWait,
  createAuthorizationFlowState,
  getRemainingSeconds,
  isAuthorizationExpired,
  retryAuthorizationWait,
  startAuthorizationWait,
} from './authorizationFlow.js';

const EXPIRES_AT = '2026-10-08T12:00:30.000Z';
const NOW = Date.parse('2026-10-08T12:00:00.000Z');

function startWaiting() {
  return startAuthorizationWait(createAuthorizationFlowState(), {
    requestId: 7,
    expiresAt: EXPIRES_AT,
    context: { saleId: 445, productId: 5, productName: 'Café molido', quantity: 2 },
    deduplicated: false,
  });
}

describe('authorizationFlow state machine', () => {
  it('1. starts idle and moves to waiting with the request data', () => {
    const initial = createAuthorizationFlowState();
    assert.strictEqual(initial.phase, AUTHORIZATION_PHASES.IDLE);

    const waiting = startWaiting();
    assert.strictEqual(waiting.phase, AUTHORIZATION_PHASES.WAITING);
    assert.strictEqual(waiting.requestId, 7);
    assert.strictEqual(waiting.expiresAt, EXPIRES_AT);
    assert.strictEqual(waiting.context.saleId, 445);
    assert.strictEqual(waiting.deduplicated, false);
  });

  it('2. approved remote resolution grants with the token', () => {
    const granted = applyAuthorizationResolution(startWaiting(), {
      approved: true,
      token: 'jwt-token',
      resolutionMode: 'Remote',
      resolvedByName: 'Admin Dos',
    });

    assert.strictEqual(granted.phase, AUTHORIZATION_PHASES.GRANTED);
    assert.strictEqual(granted.token, 'jwt-token');
    assert.strictEqual(granted.resolutionMode, 'Remote');
    assert.strictEqual(granted.resolvedByName, 'Admin Dos');
  });

  it('3. approved local resolution grants in the local phase', () => {
    const local = applyAuthorizationResolution(startWaiting(), {
      approved: true,
      token: 'local-token',
      resolutionMode: 'Local',
      resolvedByName: 'Supervisora 80',
    });

    assert.strictEqual(local.phase, AUTHORIZATION_PHASES.LOCAL);
    assert.strictEqual(local.token, 'local-token');
    assert.strictEqual(local.resolutionMode, 'Local');
  });

  it('4. rejection keeps the reason and resolves the wait', () => {
    const rejected = applyAuthorizationResolution(startWaiting(), {
      approved: false,
      reason: 'Monto fuera de política',
      resolvedByName: 'Admin Uno',
    });

    assert.strictEqual(rejected.phase, AUTHORIZATION_PHASES.REJECTED);
    assert.strictEqual(rejected.reason, 'Monto fuera de política');
    assert.strictEqual(rejected.resolvedByName, 'Admin Uno');
  });

  it('5. expiry switches waiting to expired and an unrecoverable approval also expires', () => {
    assert.strictEqual(applyAuthorizationExpiry(startWaiting()).phase, AUTHORIZATION_PHASES.EXPIRED);

    const approvedWithoutToken = applyAuthorizationResolution(startWaiting(), {
      approved: true,
      token: null,
      resolutionMode: 'Remote',
    });
    assert.strictEqual(applyAuthorizationExpiry(approvedWithoutToken).phase, AUTHORIZATION_PHASES.EXPIRED);
  });

  it('6. retry starts a brand new request and only applies from expired', () => {
    const expired = applyAuthorizationExpiry(startWaiting());
    const retried = retryAuthorizationWait(expired, {
      requestId: 8,
      expiresAt: '2026-10-08T12:01:30.000Z',
      context: { saleId: 445, productId: 5, productName: 'Café molido', quantity: 2 },
      deduplicated: false,
    });

    assert.strictEqual(retried.phase, AUTHORIZATION_PHASES.WAITING);
    assert.strictEqual(retried.requestId, 8);
    assert.strictEqual(retried.expiresAt, '2026-10-08T12:01:30.000Z');
    assert.strictEqual(retried.context.saleId, 445);

    const untouched = startWaiting();
    assert.strictEqual(retryAuthorizationWait(untouched, { requestId: 9, expiresAt: EXPIRES_AT }), untouched);
  });

  it('7. cancel returns a fresh idle state', () => {
    const cancelled = cancelAuthorizationWait();
    assert.strictEqual(cancelled.phase, AUTHORIZATION_PHASES.IDLE);
    assert.strictEqual(cancelled.requestId, null);
  });

  it('8. countdown derives remaining seconds from expiresAt and floors at zero', () => {
    assert.strictEqual(getRemainingSeconds(EXPIRES_AT, NOW), 30);
    assert.strictEqual(getRemainingSeconds(EXPIRES_AT, NOW + 500), 30);
    assert.strictEqual(getRemainingSeconds(EXPIRES_AT, NOW + 29_500), 1);
    assert.strictEqual(getRemainingSeconds(EXPIRES_AT, NOW + 30_000), 0);
    assert.strictEqual(getRemainingSeconds(EXPIRES_AT, NOW + 90_000), 0);
    assert.strictEqual(getRemainingSeconds(null, NOW), 0);

    assert.strictEqual(isAuthorizationExpired(EXPIRES_AT, NOW), false);
    assert.strictEqual(isAuthorizationExpired(EXPIRES_AT, NOW + 30_000), true);
  });

  it('9. exports the exact user-facing strings from the spec', () => {
    assert.strictEqual(AUTHORIZATION_STRINGS.waiting, 'Esperando autorización remota...');
    assert.strictEqual(AUTHORIZATION_STRINGS.localAuthorization, 'Autorización Local');
    assert.strictEqual(AUTHORIZATION_STRINGS.rejected, 'Solicitud rechazada.');
    assert.strictEqual(AUTHORIZATION_STRINGS.expired, 'Expirada');
    assert.strictEqual(AUTHORIZATION_STRINGS.invalidCredentials, 'Credenciales inválidas o sin privilegios para autorizar.');
    assert.strictEqual(AUTHORIZATION_STRINGS.retry, 'Reintentar');
    assert.strictEqual(AUTHORIZATION_STRINGS.cancel, 'Cancelar');
  });
});