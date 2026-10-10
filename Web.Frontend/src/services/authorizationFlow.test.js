import { describe, it } from 'node:test';
import assert from 'node:assert';
import {
  AUTHORIZATION_PHASES,
  AUTHORIZATION_SETTLEMENTS,
  AUTHORIZATION_STRINGS,
  applyAuthorizationExpiry,
  applyAuthorizationResolution,
  cancelAuthorizationWait,
  createAuthorizationFlowState,
  getAuthorizationSettlement,
  getRemainingSeconds,
  isAuthorizationExpired,
  isAuthorizationPending,
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

  it('8. countdown derives remaining seconds from expiresAt with a floor, never a ceiling', () => {
    assert.strictEqual(getRemainingSeconds(EXPIRES_AT, NOW), 30);
    assert.strictEqual(getRemainingSeconds(EXPIRES_AT, NOW + 500), 29);
    assert.strictEqual(getRemainingSeconds(EXPIRES_AT, NOW + 29_000), 1);
    assert.strictEqual(getRemainingSeconds(EXPIRES_AT, NOW + 29_500), 0);
    assert.strictEqual(getRemainingSeconds(EXPIRES_AT, NOW + 30_000), 0);
    assert.strictEqual(getRemainingSeconds(EXPIRES_AT, NOW + 90_000), 0);
    assert.strictEqual(getRemainingSeconds(null, NOW), 0);

    assert.strictEqual(isAuthorizationExpired(EXPIRES_AT, NOW), false);
    assert.strictEqual(isAuthorizationExpired(EXPIRES_AT, NOW + 29_500), true);
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

  it('10. settles the caller immediately on rejection with reason and resolver, keeping the ack open', () => {
    const rejected = applyAuthorizationResolution(startWaiting(), {
      approved: false,
      reason: 'Monto fuera de política',
      resolvedByName: 'Admin Uno',
    });

    assert.deepStrictEqual(
      getAuthorizationSettlement(rejected, AUTHORIZATION_SETTLEMENTS.REJECTION),
      {
        ok: false,
        outcome: 'rejected',
        reason: 'Monto fuera de política',
        resolvedByName: 'Admin Uno',
      }
    );

    const rejectedWithoutReason = applyAuthorizationResolution(startWaiting(), { approved: false });
    assert.deepStrictEqual(
      getAuthorizationSettlement(rejectedWithoutReason, AUTHORIZATION_SETTLEMENTS.REJECTION),
      { ok: false, outcome: 'rejected', reason: null, resolvedByName: null }
    );

    // El cierre posterior del acuse no vuelve a liquidar la promesa.
    assert.strictEqual(getAuthorizationSettlement(rejected, AUTHORIZATION_SETTLEMENTS.MANUAL_CANCEL), null);
    assert.strictEqual(getAuthorizationSettlement(rejected, AUTHORIZATION_SETTLEMENTS.EXPIRED_DISMISSAL), null);
  });

  it('11. expiry keeps the caller pending while a retry starts a new request that can reach granted', () => {
    const expired = applyAuthorizationExpiry(startWaiting());

    // Desestimar la expirada sin reintentar liquida expired.
    assert.deepStrictEqual(
      getAuthorizationSettlement(expired, AUTHORIZATION_SETTLEMENTS.EXPIRED_DISMISSAL),
      { ok: false, outcome: 'expired' }
    );

    // Reintentar no liquida: vuelve a waiting y la promesa del caller sigue viva.
    const retried = retryAuthorizationWait(expired, {
      requestId: 8,
      expiresAt: EXPIRES_AT,
      context: null,
      deduplicated: false,
    });
    assert.strictEqual(retried.phase, AUTHORIZATION_PHASES.WAITING);
    assert.strictEqual(getAuthorizationSettlement(retried, AUTHORIZATION_SETTLEMENTS.EXPIRED_DISMISSAL), null);

    // El reintento exitoso alcanza el outcome granted por la via de aprobacion de siempre.
    const granted = applyAuthorizationResolution(retried, { approved: true, token: 'retry-token' });
    assert.strictEqual(granted.phase, AUTHORIZATION_PHASES.GRANTED);
    assert.strictEqual(granted.token, 'retry-token');
  });

  it('12. manual cancel settles cancelled and is distinguishable from rejection and expiry', () => {
    const cancelled = getAuthorizationSettlement(startWaiting(), AUTHORIZATION_SETTLEMENTS.MANUAL_CANCEL);

    assert.deepStrictEqual(cancelled, { ok: false, cancelled: true });
    assert.ok(!('outcome' in cancelled));
    assert.notDeepStrictEqual(cancelled, getAuthorizationSettlement(
      applyAuthorizationResolution(startWaiting(), { approved: false }),
      AUTHORIZATION_SETTLEMENTS.REJECTION
    ));

    // Triggers cruzados no liquidan: la fase manda.
    assert.strictEqual(getAuthorizationSettlement(startWaiting(), AUTHORIZATION_SETTLEMENTS.REJECTION), null);
    assert.strictEqual(
      getAuthorizationSettlement(applyAuthorizationExpiry(startWaiting()), AUTHORIZATION_SETTLEMENTS.MANUAL_CANCEL),
      null
    );
    assert.strictEqual(getAuthorizationSettlement(applyAuthorizationResolution(startWaiting(), { approved: false }), AUTHORIZATION_SETTLEMENTS.EXPIRED_DISMISSAL), null);
    assert.strictEqual(getAuthorizationSettlement(null, AUTHORIZATION_SETTLEMENTS.MANUAL_CANCEL), null);
  });

  it('13. only a waiting request with an id is cancellable on the server', () => {
    const waiting = startWaiting();
    assert.strictEqual(isAuthorizationPending(waiting), true);

    assert.strictEqual(isAuthorizationPending(applyAuthorizationExpiry(waiting)), false);
    assert.strictEqual(isAuthorizationPending(applyAuthorizationResolution(waiting, { approved: false })), false);
    assert.strictEqual(isAuthorizationPending(applyAuthorizationResolution(waiting, { approved: true, token: 't' })), false);
    assert.strictEqual(isAuthorizationPending(createAuthorizationFlowState()), false);
    assert.strictEqual(
      isAuthorizationPending(startAuthorizationWait(createAuthorizationFlowState(), { requestId: null, expiresAt: EXPIRES_AT })),
      false
    );
    assert.strictEqual(isAuthorizationPending(null), false);
  });
});