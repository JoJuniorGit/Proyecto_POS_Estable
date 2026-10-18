// 8.187-T5 (Paso 11/12, F4, S1-S7): contrato HTTP del crédito CxC en el POS web.
// Verifica rutas exactas, headers (token de autorización e idempotencia), construcción de
// filtros de cartera, payloads y el parseo del 403 authorizationAction=CreditOverLimit.
import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert';
import {
  getCreditProfile,
  updateCreditProfile,
  getCreditStatement,
  buildCreditReceivablesQuery,
  getCreditReceivables,
  completeCreditSale,
  registerCreditPayment,
  cancelCreditSale,
  registerCreditReturn,
  getCreditSaleReceipt,
  getCreditPaymentReceipt,
  isCreditOverLimitError,
  extractCreditOverLimitContext,
  CREDIT_OVER_LIMIT_ACTION,
} from './creditApi.js';

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

describe('creditApi — perfil, cartera y estado de cuenta', () => {
  let originalFetch;
  let fetchCalls;

  beforeEach(() => {
    originalFetch = global.fetch;
    fetchCalls = [];
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return jsonResponse({ ok: true });
    };
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  it('1. getCreditProfile GETs /api/sales/customers/{id}/credit-profile', async () => {
    await getCreditProfile(7);

    assert.strictEqual(fetchCalls.length, 1);
    assert.strictEqual(fetchCalls[0].config.method, 'GET');
    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/sales/customers/7/credit-profile');
  });

  it('2. updateCreditProfile PUTs the administrative payload with numeric creditStatus', async () => {
    await updateCreditProfile(7, {
      isCreditEnabled: true,
      creditLimitUSD: 250.5,
      paymentTermDays: 15,
      creditGraceDays: 3,
      creditNotes: '  Acuerdo especial  ',
      creditStatus: 2,
    });

    assert.strictEqual(fetchCalls[0].config.method, 'PUT');
    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/sales/customers/7/credit-profile');
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), {
      isCreditEnabled: true,
      creditLimitUSD: 250.5,
      paymentTermDays: 15,
      creditGraceDays: 3,
      creditNotes: 'Acuerdo especial',
      creditStatus: 2,
    });
  });

  it('3. updateCreditProfile normalizes blank notes to null (server contract)', async () => {
    await updateCreditProfile(7, {
      isCreditEnabled: false,
      creditLimitUSD: 0,
      paymentTermDays: 30,
      creditGraceDays: 0,
      creditNotes: '   ',
      creditStatus: 0,
    });

    assert.strictEqual(JSON.parse(fetchCalls[0].config.body).creditNotes, null);
  });

  it('4. getCreditStatement GETs /api/sales/customers/{id}/credit-statement', async () => {
    await getCreditStatement(12);

    assert.strictEqual(fetchCalls[0].config.method, 'GET');
    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/sales/customers/12/credit-statement');
  });
});

describe('creditApi — filtros de cartera global', () => {
  let originalFetch;
  let fetchCalls;

  beforeEach(() => {
    originalFetch = global.fetch;
    fetchCalls = [];
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return jsonResponse({ items: [] });
    };
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  it('5. builds the bare route without filters', () => {
    assert.strictEqual(buildCreditReceivablesQuery(), '/api/sales/credit-receivables');
    assert.strictEqual(buildCreditReceivablesQuery({}), '/api/sales/credit-receivables');
  });

  it('6. encodes customer, status, mora and search filters', () => {
    const url = buildCreditReceivablesQuery({
      customerId: 7,
      status: 'Vencida',
      overdueOnly: true,
      search: ' José Pérez ',
    });

    assert.strictEqual(
      url,
      '/api/sales/credit-receivables?customerId=7&status=Vencida&overdueOnly=true&search=Jos%C3%A9%20P%C3%A9rez'
    );
  });

  it('7. omits empty filters and only sends overdueOnly when true', () => {
    assert.strictEqual(
      buildCreditReceivablesQuery({ customerId: null, status: '   ', overdueOnly: false, search: '  ' }),
      '/api/sales/credit-receivables'
    );
  });

  it('8. getCreditReceivables GETs the built filter route', async () => {
    await getCreditReceivables({ status: 'Parcial' });

    assert.strictEqual(fetchCalls[0].config.method, 'GET');
    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/sales/credit-receivables?status=Parcial');
  });
});

describe('creditApi — cierre a crédito, abonos, anulación y devolución', () => {
  let originalFetch;
  let fetchCalls;

  beforeEach(() => {
    originalFetch = global.fetch;
    fetchCalls = [];
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return jsonResponse(123);
    };
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  it('9. completeCreditSale POSTs the exact route/body and omits the token header by default', async () => {
    const payments = [{ paymentMethodId: 1, amount: 20, amountBsS: 730, amountLocal: 730, referenceNumber: null }];
    await completeCreditSale(9, { exchangeRate: 36.5, cashierId: 4, payments });

    assert.strictEqual(fetchCalls[0].config.method, 'POST');
    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/sales/9/complete-credit');
    assert.ok(!('X-Authorization-Token' in fetchCalls[0].config.headers), 'sin token no se envía el header');
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), {
      exchangeRate: 36.5,
      cashierId: 4,
      payments,
    });
  });

  it('10. completeCreditSale sends X-Authorization-Token when the wait flow grants one', async () => {
    await completeCreditSale(9, { exchangeRate: 36.5, payments: [], authorizationToken: 'tok-1' });

    assert.strictEqual(fetchCalls[0].config.headers['X-Authorization-Token'], 'tok-1');
  });

  it('11. registerCreditPayment posts the body key and the Idempotency-Key header (FIFO: receivableIds null)', async () => {
    await registerCreditPayment(
      { customerId: 7, amountUSD: 120.5, paymentMethodId: 2, referenceNumber: 'REF-1', receivableIds: [] },
      'idem-1'
    );

    assert.strictEqual(fetchCalls[0].config.method, 'POST');
    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/sales/credit-payments');
    assert.strictEqual(fetchCalls[0].config.headers['Idempotency-Key'], 'idem-1');
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), {
      customerId: 7,
      amountUSD: 120.5,
      paymentMethodId: 2,
      referenceNumber: 'REF-1',
      receivableIds: null,
      idempotencyKey: 'idem-1',
    });
  });

  it('12. registerCreditPayment keeps explicit invoice allocation order', async () => {
    await registerCreditPayment({
      customerId: 7,
      amountUSD: 50,
      paymentMethodId: 2,
      referenceNumber: null,
      receivableIds: [11, 12],
    });

    const body = JSON.parse(fetchCalls[0].config.body);
    assert.deepStrictEqual(body.receivableIds, [11, 12]);
    assert.ok(typeof body.idempotencyKey === 'string' && body.idempotencyKey.length > 0, 'la clave se autogenera');
    assert.strictEqual(fetchCalls[0].config.headers['Idempotency-Key'], body.idempotencyKey);
  });

  it('13. cancelCreditSale POSTs the reason (trimmed, null when empty)', async () => {
    await cancelCreditSale(9, '  error de digitación  ');
    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/sales/9/credit-cancel');
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), { reason: 'error de digitación' });

    fetchCalls = [];
    await cancelCreditSale(9);
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), { reason: null });
  });

  it('14. registerCreditReturn POSTs items/reason with a stable Idempotency-Key header', async () => {
    await registerCreditReturn(
      9,
      { items: [{ saleItemId: 3, quantity: 2 }], reason: ' dañado ' },
      'ret-key'
    );

    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/sales/9/credit-return');
    assert.strictEqual(fetchCalls[0].config.headers['Idempotency-Key'], 'ret-key');
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), {
      items: [{ saleItemId: 3, quantity: 2 }],
      reason: 'dañado',
      idempotencyKey: 'ret-key',
    });
  });
});

describe('creditApi — comprobantes PDF', () => {
  let originalFetch;
  let fetchCalls;

  beforeEach(() => {
    originalFetch = global.fetch;
    fetchCalls = [];
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return blobResponse('credit-pdf');
    };
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  it('15. getCreditSaleReceipt GETs the credit receipt as blob', async () => {
    const blob = await getCreditSaleReceipt(9);

    assert.strictEqual(blob, 'credit-pdf');
    assert.strictEqual(fetchCalls[0].config.method, 'GET');
    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/sales/9/credit-receipt');
  });

  it('16. getCreditPaymentReceipt GETs the payment receipt as blob', async () => {
    const blob = await getCreditPaymentReceipt(55);

    assert.strictEqual(blob, 'credit-pdf');
    assert.strictEqual(fetchCalls[0].config.method, 'GET');
    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/sales/credit-payments/55/receipt');
  });
});

describe('creditApi — 403 de sobregiro (authorizationAction=CreditOverLimit)', () => {
  let originalFetch;

  beforeEach(() => {
    originalFetch = global.fetch;
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  it('17. surfaces the actionable error and the exact context from the ProblemDetails body', async () => {
    global.fetch = async () => jsonResponse({
      title: 'Forbidden',
      status: 403,
      detail: 'El monto financiado excede el cupo disponible. Se requiere autorización de un Administrador o Supervisor.',
      authorizationRequired: true,
      authorizationAction: 'CreditOverLimit',
      authorizationContext: {
        saleId: 9,
        customerId: 7,
        customerName: 'Ana Pérez',
        financedUSD: 200.5,
        availableCreditUSD: 50,
      },
    }, 403, 'Forbidden');

    await assert.rejects(
      () => completeCreditSale(9, { exchangeRate: 36.5, payments: [] }),
      (err) => {
        assert.strictEqual(CREDIT_OVER_LIMIT_ACTION, 'CreditOverLimit');
        assert.strictEqual(isCreditOverLimitError(err), true);
        assert.deepStrictEqual(extractCreditOverLimitContext(err), {
          saleId: 9,
          customerId: 7,
          customerName: 'Ana Pérez',
          financedUSD: 200.5,
          availableCreditUSD: 50,
        });
        return true;
      }
    );
  });

  it('18. keeps ordinary errors outside the over-limit contract', async () => {
    global.fetch = async () => jsonResponse({
      title: 'Conflict',
      status: 409,
      detail: 'La obligación tiene abonos registrados.',
    }, 409, 'Conflict');

    await assert.rejects(
      () => cancelCreditSale(9, 'motivo'),
      (err) => {
        assert.strictEqual(isCreditOverLimitError(err), false);
        assert.strictEqual(extractCreditOverLimitContext(err), null);
        return true;
      }
    );
  });
});
