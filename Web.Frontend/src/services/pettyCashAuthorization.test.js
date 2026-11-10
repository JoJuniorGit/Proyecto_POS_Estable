// Residual 3 (8.188, S4/S5): flujo web de autorizacion de caja chica.
// Contexto canonico traducido del 403 (sesion/divisa/montos bimonetarios/categoria), espera
// aprobada/rechazada/expirada/cancelada, reintento UNICO con X-Authorization-Token y la MISMA
// Idempotency-Key del intento original.
import { describe, it } from 'node:test';
import assert from 'node:assert';
import {
  buildPettyCashExpenseAuthorizationContext,
  getWebTerminalLabel,
  submitPettyCashWithAuthorization,
} from './pettyCashAuthorization.js';
import { buildAuthorizationRequestPayload } from './authorizationHub.js';
import { ApiError } from './api.js';

const CONTEXT_403 = Object.freeze({
  sessionId: 42,
  currency: 1,
  amountBss: 1825,
  amountUsd: 50,
  category: 2,
});

function pettyCash403(overrides = {}) {
  return new ApiError('El gasto de caja chica requiere autorización de un supervisor antes de asentarse.', 403, {
    title: 'Forbidden',
    status: 403,
    authorizationRequired: true,
    authorizationAction: 'PettyCashExpense',
    authorizationContext: { ...CONTEXT_403, ...overrides },
  });
}

const AUTHORIZED_CONTEXT = Object.freeze({
  actionType: 4,
  pettyCashSessionId: 42,
  pettyCashCurrency: 1,
  pettyCashAmountBsS: 1825,
  pettyCashAmountUsd: 50,
  pettyCashCategory: 2,
  terminal: 'Caja-Web-01',
});

/** startWait falso: simula la aprobacion remota del provider (retry con token). */
function approvedStartWait(capture = {}) {
  return async ({ context, retry }) => {
    capture.context = context;
    capture.calls = (capture.calls ?? 0) + 1;
    await retry('tok-1');
    return { ok: true, token: 'tok-1', resolutionMode: 'Remote', resolvedByName: 'Admin Uno' };
  };
}

describe('pettyCashAuthorization — contexto canonico del 403', () => {
  it('1. maps the exact 403 context into the hub/REST request contract without a sale', () => {
    const context = buildPettyCashExpenseAuthorizationContext(CONTEXT_403, { terminal: 'Caja-Web-01' });
    assert.deepStrictEqual(context, AUTHORIZED_CONTEXT);

    const payload = buildAuthorizationRequestPayload(context);
    assert.deepStrictEqual(payload, AUTHORIZED_CONTEXT);
    assert.ok(!('saleId' in payload), 'la caja chica no tiene venta: el servidor persiste SaleId NULL');
  });

  it('1b. accepts the amountBsS alias and stable keys of the 403 contract', () => {
    const context = buildPettyCashExpenseAuthorizationContext(
      { ...CONTEXT_403, amountBss: undefined, amountBsS: 1825 },
      { terminal: 'Caja-Web-02' }
    );

    assert.strictEqual(context.pettyCashAmountBsS, 1825);
    assert.strictEqual(context.pettyCashAmountUsd, 50);
    assert.strictEqual(context.terminal, 'Caja-Web-02');
  });

  it('2. fails closed when the 403 context is missing, incomplete or out of the catalog', () => {
    const invalidContexts = [
      null,
      undefined,
      'nope',
      {},
      { ...CONTEXT_403, sessionId: 0 },
      { ...CONTEXT_403, sessionId: null },
      { ...CONTEXT_403, currency: 9 },
      { ...CONTEXT_403, category: 9 },
      { ...CONTEXT_403, amountBss: 0 },
      { ...CONTEXT_403, amountBss: 'x' },
      { ...CONTEXT_403, amountUsd: -5 },
      { ...CONTEXT_403, amountUsd: null },
    ];

    for (const invalid of invalidContexts) {
      assert.strictEqual(
        buildPettyCashExpenseAuthorizationContext(invalid, { terminal: 'Caja-Web-01' }),
        null,
        `contexto inválido debe fallar cerrado: ${JSON.stringify(invalid)}`
      );
    }
  });

  it('3. resolves the terminal label from the web host with a safe fallback', () => {
    assert.strictEqual(getWebTerminalLabel(null), 'Web');
    assert.strictEqual(getWebTerminalLabel({}), 'Web');
    assert.strictEqual(getWebTerminalLabel({ location: { hostname: '' } }), 'Web');
    assert.strictEqual(getWebTerminalLabel({ location: { hostname: 'caja-01.local' } }), 'caja-01.local');
  });
});

describe('pettyCashAuthorization — intento, espera y reintento unico', () => {
  it('4. a first-attempt success never opens the wait flow', async () => {
    let startWaitCalls = 0;
    const outcome = await submitPettyCashWithAuthorization({
      submit: async () => ({ id: 5, voucherNumber: 1 }),
      startWait: async () => { startWaitCalls += 1; return { ok: true }; },
      getIdempotencyKey: () => 'pk-1',
      terminal: 'Caja-Web-01',
    });

    assert.deepStrictEqual(outcome, { ok: true, result: { id: 5, voucherNumber: 1 } });
    assert.strictEqual(startWaitCalls, 0);
  });

  it('5. a 403 opens the wait with the exact context and retries ONCE with token + same key', async () => {
    const submits = [];
    const capture = {};
    const outcome = await submitPettyCashWithAuthorization({
      submit: async (options) => {
        submits.push(options);
        if (submits.length === 1) throw pettyCash403();
        return { id: 9, voucherNumber: 3 };
      },
      startWait: approvedStartWait(capture),
      getIdempotencyKey: () => 'pk-7',
      terminal: 'Caja-Web-01',
    });

    assert.deepStrictEqual(outcome, { ok: true, result: { id: 9, voucherNumber: 3 } });
    assert.deepStrictEqual(capture.context, AUTHORIZED_CONTEXT);
    assert.strictEqual(capture.calls, 1, 'una sola espera');
    assert.deepStrictEqual(submits, [
      { idempotencyKey: 'pk-7' },
      { idempotencyKey: 'pk-7', authorizationToken: 'tok-1' },
    ], 'el reintento conserva la MISMA Idempotency-Key y agrega el token');
  });

  it('6. a rejected wait settles with the clear rejection message and no blind retry', async () => {
    let submitCalls = 0;
    const outcome = await submitPettyCashWithAuthorization({
      submit: async () => { submitCalls += 1; throw pettyCash403(); },
      startWait: async () => ({ ok: false, outcome: 'rejected', reason: 'Monto fuera de política' }),
      getIdempotencyKey: () => 'pk-2',
    });

    assert.deepStrictEqual(outcome, {
      ok: false,
      message: 'La autorización fue rechazada: Monto fuera de política',
    });
    assert.strictEqual(submitCalls, 1, 'el rechazo no reintenta la accion');

    const withoutReason = await submitPettyCashWithAuthorization({
      submit: async () => { throw pettyCash403(); },
      startWait: async () => ({ ok: false, outcome: 'rejected', reason: null }),
      getIdempotencyKey: () => 'pk-2',
    });
    assert.strictEqual(withoutReason.message, 'La autorización fue rechazada.');
  });

  it('7. an expired wait (timeout without token) settles as expired', async () => {
    const outcome = await submitPettyCashWithAuthorization({
      submit: async () => { throw pettyCash403(); },
      startWait: async () => ({ ok: false, outcome: 'expired' }),
      getIdempotencyKey: () => 'pk-3',
    });

    assert.deepStrictEqual(outcome, {
      ok: false,
      message: 'La solicitud de autorización expiró. Intente nuevamente.',
    });
  });

  it('8. an operator cancel settles as cancelled', async () => {
    const outcome = await submitPettyCashWithAuthorization({
      submit: async () => { throw pettyCash403(); },
      startWait: async () => ({ ok: false, cancelled: true }),
      getIdempotencyKey: () => 'pk-4',
    });

    assert.deepStrictEqual(outcome, { ok: false, message: 'La autorización fue cancelada.' });
  });

  it('9. a 403 without a usable context fails closed with the informative message and no request', async () => {
    let startWaitCalls = 0;
    const withoutContext = new ApiError('Forbidden', 403, {
      status: 403,
      authorizationRequired: true,
      authorizationAction: 'PettyCashExpense',
    });

    const outcome = await submitPettyCashWithAuthorization({
      submit: async () => { throw withoutContext; },
      startWait: async () => { startWaitCalls += 1; return { ok: true }; },
      getIdempotencyKey: () => 'pk-5',
    });

    assert.strictEqual(outcome.ok, false);
    assert.match(outcome.message, /Administrador o Supervisor/);
    assert.match(outcome.message, /reintente la operación/);
    assert.doesNotMatch(outcome.message, /WPF/);
    assert.strictEqual(startWaitCalls, 0, 'sin contexto completo no se crea la solicitud');
  });

  it('10. a wait creation failure surfaces the real error without retrying the action', async () => {
    let submitCalls = 0;
    const outcome = await submitPettyCashWithAuthorization({
      submit: async () => { submitCalls += 1; throw pettyCash403(); },
      startWait: async () => { throw new ApiError('No se pudo crear la solicitud de autorización.', 500, null); },
      getIdempotencyKey: () => 'pk-6',
    });

    assert.deepStrictEqual(outcome, { ok: false, message: 'No se pudo crear la solicitud de autorización.' });
    assert.strictEqual(submitCalls, 1);
  });

  it('11. ordinary errors are rethrown and never enter the authorization flow', async () => {
    const conflict = new ApiError('Saldo de efectivo físico en Bs.S insuficiente.', 409, { status: 409 });

    await assert.rejects(
      () => submitPettyCashWithAuthorization({
        submit: async () => { throw conflict; },
        startWait: async () => ({ ok: true }),
        getIdempotencyKey: () => 'pk-8',
      }),
      (error) => error === conflict
    );
  });

  it('12. the cancellation path works without an idempotency key (token-only retry)', async () => {
    const submits = [];
    const outcome = await submitPettyCashWithAuthorization({
      submit: async (options) => {
        submits.push(options);
        if (submits.length === 1) throw pettyCash403();
        return { id: 7, status: 1 };
      },
      startWait: approvedStartWait(),
    });

    assert.deepStrictEqual(outcome, { ok: true, result: { id: 7, status: 1 } });
    assert.deepStrictEqual(submits, [{}, { authorizationToken: 'tok-1' }]);
  });

  it('13. the service contract fails fast when dependencies are missing', async () => {
    await assert.rejects(() => submitPettyCashWithAuthorization({}), /requiere/);
    await assert.rejects(
      () => submitPettyCashWithAuthorization({ submit: async () => ({}) }),
      /requiere/
    );
  });
});
