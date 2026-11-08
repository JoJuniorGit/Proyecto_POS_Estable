// T5 (Paso 13 / 8.188, S1/S4/S5/S7): contrato HTTP de caja chica en el POS web.
// Verifica rutas exactas, payloads bimonetarios, headers (idempotencia y token supervisor),
// el parseo del 403 authorizationAction=PettyCashExpense y la descarga del vale PDF.
import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert';
import {
  PETTY_CASH_AUTHORIZATION_ACTION,
  PettyCashCurrency,
  PettyCashStatus,
  registerPettyCashExpense,
  cancelPettyCashExpense,
  getPettyCashExpenses,
  getPettyCashVoucher,
  getPettyCashPhysicalBalances,
  openCashDrawerSession,
  closeCashDrawerSession,
  isPettyCashAuthorizationError,
  extractPettyCashAuthorizationContext,
  buildPettyCashAuthorizationMessage,
  getPettyCashCategoryLabel,
  getPettyCashCurrencyLabel,
  getPettyCashStatusLabel,
} from './pettyCashApi.js';

function jsonResponse(payload, status = 200, statusText = 'OK') {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText,
    headers: { get: (h) => (h.toLowerCase() === 'content-type' ? 'application/json' : null) },
    text: async () => JSON.stringify(payload),
    json: async () => payload,
  };
}

function blobResponse(payload = 'pdf-bytes') {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    headers: { get: () => 'application/pdf' },
    blob: async () => payload,
    text: async () => String(payload),
  };
}

describe('pettyCashApi — enums y etiquetas del contrato', () => {
  it('1. mirrors the persisted enum ids and the Spanish category labels', () => {
    assert.strictEqual(PettyCashCurrency.BsS, 0);
    assert.strictEqual(PettyCashCurrency.Usd, 1);
    assert.strictEqual(PettyCashStatus.Active, 0);
    assert.strictEqual(PettyCashStatus.Cancelled, 1);

    assert.strictEqual(getPettyCashCategoryLabel(0), 'Insumos y Limpieza');
    assert.strictEqual(getPettyCashCategoryLabel(1), 'Fletes y Acarreo');
    assert.strictEqual(getPettyCashCategoryLabel(2), 'Mantenimiento y Reparaciones');
    assert.strictEqual(getPettyCashCategoryLabel(3), 'Servicios Públicos/Eventuales');
    assert.strictEqual(getPettyCashCategoryLabel(4), 'Papelería');
    assert.strictEqual(getPettyCashCategoryLabel(5), 'Varios');
    assert.strictEqual(getPettyCashCategoryLabel(99), 'Varios');

    assert.strictEqual(getPettyCashCurrencyLabel(PettyCashCurrency.Usd), 'USD');
    assert.strictEqual(getPettyCashCurrencyLabel(PettyCashCurrency.BsS), 'Bs.S');
    assert.strictEqual(getPettyCashStatusLabel(PettyCashStatus.Cancelled), 'Anulado');
    assert.strictEqual(getPettyCashStatusLabel(PettyCashStatus.Active), 'Activo');
  });
});

describe('pettyCashApi — registro, anulación, listado y saldos', () => {
  let originalFetch;
  let fetchCalls;

  beforeEach(() => {
    originalFetch = global.fetch;
    fetchCalls = [];
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return jsonResponse({ id: 1, voucherNumber: 1 });
    };
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  it('2. register POSTs the nominal payload with the idempotency key in body and header', async () => {
    await registerPettyCashExpense(
      {
        currency: PettyCashCurrency.Usd,
        amount: 25.5,
        category: 2,
        concept: '  Compra de tornillos  ',
        beneficiary: '  Ferretería El Tornillo  ',
        referenceNumber: '  FAC-001  ',
      },
      { idempotencyKey: 'pk-1' }
    );

    assert.strictEqual(fetchCalls[0].config.method, 'POST');
    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/cashdrawer/petty-cash');
    assert.strictEqual(fetchCalls[0].config.headers['Idempotency-Key'], 'pk-1');
    assert.ok(
      !('X-Authorization-Token' in fetchCalls[0].config.headers),
      'sin autorización no se envía el header del token'
    );
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), {
      currency: 1,
      amount: 25.5,
      category: 2,
      concept: 'Compra de tornillos',
      beneficiary: 'Ferretería El Tornillo',
      referenceNumber: 'FAC-001',
      idempotencyKey: 'pk-1',
    });
  });

  it('3. register sends X-Authorization-Token only when a token is supplied', async () => {
    await registerPettyCashExpense(
      { currency: 0, amount: 100, category: 5, concept: 'c', beneficiary: 'b', referenceNumber: 'r' },
      { idempotencyKey: 'pk-2', authorizationToken: 'tok-9' }
    );

    assert.strictEqual(fetchCalls[0].config.headers['X-Authorization-Token'], 'tok-9');
    assert.strictEqual(fetchCalls[0].config.headers['Idempotency-Key'], 'pk-2');
  });

  it('3b. the authorized retry replays the same idempotency key with the token header', async () => {
    const draft = { currency: 1, amount: 50, category: 2, concept: 'c', beneficiary: 'b', referenceNumber: 'r' };

    await registerPettyCashExpense(draft, { idempotencyKey: 'pk-attempt' });
    await registerPettyCashExpense(draft, { idempotencyKey: 'pk-attempt', authorizationToken: 'tok-1' });

    assert.strictEqual(fetchCalls.length, 2);
    assert.strictEqual(fetchCalls[0].config.headers['Idempotency-Key'], 'pk-attempt');
    assert.ok(!('X-Authorization-Token' in fetchCalls[0].config.headers));
    assert.strictEqual(fetchCalls[1].config.headers['Idempotency-Key'], 'pk-attempt');
    assert.strictEqual(fetchCalls[1].config.headers['X-Authorization-Token'], 'tok-1');
    assert.strictEqual(JSON.parse(fetchCalls[1].config.body).idempotencyKey, 'pk-attempt');
  });

  it('4. cancel POSTs the mandatory reason to the cancel route with optional token', async () => {
    await cancelPettyCashExpense(7, '  comprobante duplicado  ');
    assert.strictEqual(fetchCalls[0].config.method, 'POST');
    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/cashdrawer/petty-cash/7/cancel');
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), { reason: 'comprobante duplicado' });
    assert.ok(!('X-Authorization-Token' in fetchCalls[0].config.headers));

    fetchCalls = [];
    await cancelPettyCashExpense(7, 'motivo', { authorizationToken: 'tok-1' });
    assert.strictEqual(fetchCalls[0].config.headers['X-Authorization-Token'], 'tok-1');
  });

  it('5. list GETs the session-scoped collection route', async () => {
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return jsonResponse([{ id: 3 }]);
    };

    const rows = await getPettyCashExpenses(42);

    assert.deepStrictEqual(rows, [{ id: 3 }]);
    assert.strictEqual(fetchCalls[0].config.method, 'GET');
    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/cashdrawer/petty-cash?sessionId=42');
  });

  it('6. physical balances GET the segregated dual-drawer route', async () => {
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return jsonResponse({ bsS: 1500.25, usd: 30 });
    };

    const balances = await getPettyCashPhysicalBalances(42);

    assert.deepStrictEqual(balances, { bsS: 1500.25, usd: 30 });
    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/cashdrawer/physical-balances?sessionId=42');
  });

  it('7. voucher GETs the authenticated PDF as blob', async () => {
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return blobResponse('voucher-pdf');
    };

    const blob = await getPettyCashVoucher(12);

    assert.strictEqual(blob, 'voucher-pdf');
    assert.strictEqual(fetchCalls[0].config.method, 'GET');
    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/cashdrawer/petty-cash/12/receipt');
  });
});

describe('pettyCashApi — cierre de sesión con conteo USD opcional (T2b/S2)', () => {
  let originalFetch;
  let fetchCalls;

  beforeEach(() => {
    originalFetch = global.fetch;
    fetchCalls = [];
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return jsonResponse({ id: 1, closingBalanceUsd: null });
    };
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  it('8. sends actualClosingBalanceUsd: null when the optional USD count is omitted', async () => {
    await closeCashDrawerSession(
      { actualClosingBalanceLocal: 1250.5, currentExchangeRate: 36.5 },
      { idempotencyKey: 'close-attempt-1' },
    );

    assert.strictEqual(fetchCalls[0].config.method, 'POST');
    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/cashdrawer/close');
    assert.strictEqual(fetchCalls[0].config.headers['Idempotency-Key'], 'close-attempt-1');
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), {
      actualClosingBalanceLocal: 1250.5,
      currentExchangeRate: 36.5,
      actualClosingBalanceUsd: null,
    });
  });

  it('9. maps the optional USD count into actualClosingBalanceUsd', async () => {
    await closeCashDrawerSession({
      actualClosingBalanceLocal: 1250.5,
      currentExchangeRate: 36.5,
      actualClosingBalanceUsd: 20,
    });

    assert.ok(fetchCalls[0].config.headers['Idempotency-Key']);
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), {
      actualClosingBalanceLocal: 1250.5,
      currentExchangeRate: 36.5,
      actualClosingBalanceUsd: 20,
    });
  });

  it('10. opens the drawer session with both currency funds', async () => {
    await openCashDrawerSession({
      openingBalanceLocal: 1250.5,
      openingBalanceUsd: 20,
      currentExchangeRate: 36.5,
    }, { idempotencyKey: 'open-attempt-1' });

    assert.strictEqual(fetchCalls[0].config.method, 'POST');
    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/cashdrawer/open');
    assert.strictEqual(fetchCalls[0].config.headers['Idempotency-Key'], 'open-attempt-1');
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), {
      openingBalanceLocal: 1250.5,
      openingBalanceUsd: 20,
      currentExchangeRate: 36.5,
    });
  });

  it('11. opens with the explicit per-method breakdown when provided', async () => {
    await openCashDrawerSession({
      openingBalanceLocal: 150,
      openingBalanceUsd: 20,
      currentExchangeRate: 36.5,
      methodBalances: [
        { paymentMethodId: 10, openingAmount: 100 },
        { paymentMethodId: 11, openingAmount: 50 },
        { paymentMethodId: 12, openingAmount: 20 },
      ],
    }, { idempotencyKey: 'open-methods-1' });

    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), {
      openingBalanceLocal: 150,
      openingBalanceUsd: 20,
      currentExchangeRate: 36.5,
      methodBalances: [
        { paymentMethodId: 10, openingAmount: 100 },
        { paymentMethodId: 11, openingAmount: 50 },
        { paymentMethodId: 12, openingAmount: 20 },
      ],
    });
  });
});

describe('pettyCashApi — 403 authorizationAction=PettyCashExpense', () => {
  let originalFetch;

  beforeEach(() => {
    originalFetch = global.fetch;
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  it('10. surfaces the exact context and the fail-closed authorization guidance', async () => {
    global.fetch = async () => jsonResponse({
      title: 'Forbidden',
      status: 403,
      detail: 'El rol del usuario requiere autorización de caja chica. Se requiere autorización de un Administrador o Supervisor.',
      authorizationRequired: true,
      authorizationAction: 'PettyCashExpense',
      authorizationContext: {
        sessionId: 42,
        currency: 1,
        amountBss: 1825,
        amountUsd: 50,
        category: 2,
      },
    }, 403, 'Forbidden');

    await assert.rejects(
      () => registerPettyCashExpense(
        { currency: 1, amount: 50, category: 2, concept: 'c', beneficiary: 'b', referenceNumber: 'r' },
        { idempotencyKey: 'pk-3' }
      ),
      (err) => {
        assert.strictEqual(PETTY_CASH_AUTHORIZATION_ACTION, 'PettyCashExpense');
        assert.strictEqual(isPettyCashAuthorizationError(err), true);
        assert.deepStrictEqual(extractPettyCashAuthorizationContext(err), {
          sessionId: 42,
          currency: 1,
          amountBss: 1825,
          amountUsd: 50,
          category: 2,
        });

        const message = buildPettyCashAuthorizationMessage(err);
        assert.match(message, /Administrador o Supervisor/);
        assert.match(message, /reintente la operación/);
        assert.doesNotMatch(message, /WPF/);
        assert.match(message, /Sesión #42/);
        return true;
      }
    );
  });

  it('11. keeps ordinary errors outside the petty cash authorization contract', async () => {
    global.fetch = async () => jsonResponse({
      title: 'Conflict',
      status: 409,
      detail: 'Saldo de efectivo físico en Bs.S insuficiente en la gaveta para registrar el gasto de caja chica.',
    }, 409, 'Conflict');

    await assert.rejects(
      () => registerPettyCashExpense(
        { currency: 0, amount: 999999, category: 5, concept: 'c', beneficiary: 'b', referenceNumber: 'r' },
        { idempotencyKey: 'pk-4' }
      ),
      (err) => {
        assert.strictEqual(isPettyCashAuthorizationError(err), false);
        assert.strictEqual(extractPettyCashAuthorizationContext(err), null);
        assert.match(err.message, /insuficiente/);
        return true;
      }
    );
  });
});
