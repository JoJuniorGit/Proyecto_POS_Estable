// F4-WU2 (Paso 14): contrato HTTP del Estado de Resultados y de las reglas de comisión.
// El backend expone /api/profitability (Admin/Manager) y /api/payment-method-fees (Admin).
import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert';
import {
  PROFITABILITY_GROUP_BY,
  PROFITABILITY_PRESET,
  PROFITABILITY_SALE_TYPE,
  buildProfitabilityQuery,
  createFeeRule,
  deleteFeeRule,
  exportProfitability,
  getFeeRules,
  getProfitabilityBreakdown,
  getProfitabilitySummary,
  resolveExportFileName,
  updateFeeRule,
} from './profitabilityApi.js';

function jsonResponse(payload, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: 'OK',
    headers: {
      get: (header) => (header.toLowerCase() === 'content-type' ? 'application/json' : null),
    },
    text: async () => JSON.stringify(payload),
    json: async () => payload,
  };
}

function blobResponse(blob, options = {}) {
  const {
    status = 200,
    contentType = 'text/csv',
    contentDisposition = 'attachment; filename="Estado_Resultados_20261101_20261110.csv"',
  } = options;

  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: 'OK',
    headers: {
      get: (header) => {
        const key = header.toLowerCase();
        if (key === 'content-type') return contentType;
        if (key === 'content-disposition') return contentDisposition;
        return null;
      },
    },
    blob: async () => blob,
    text: async () => '',
  };
}

function noContentResponse() {
  return {
    ok: true,
    status: 204,
    statusText: 'No Content',
    headers: { get: () => null },
  };
}

describe('profitabilityApi — query building', () => {
  it('1. builds the full filter query with repeated ids and numeric enums', () => {
    const query = buildProfitabilityQuery({
      preset: 'Custom',
      fromDate: '2026-11-01',
      toDate: '2026-11-10',
      categoryIds: [3, 7],
      includeCategorySubtree: false,
      productIds: [11],
      paymentMethodIds: [2, 5],
      cashierId: 9,
      saleType: 'CashAdvance',
      priceListType: 'Mayorista',
      groupBy: 'Week',
    });

    assert.strictEqual(
      query,
      'preset=4&fromDate=2026-11-01&toDate=2026-11-10&categoryIds=3&categoryIds=7&includeCategorySubtree=false&productIds=11&paymentMethodIds=2&paymentMethodIds=5&cashierId=9&saleType=1&priceListType=Mayorista&groupBy=2'
    );
  });

  it('2. keeps the backend defaults when the filter is empty', () => {
    assert.strictEqual(
      buildProfitabilityQuery(),
      'preset=0&includeCategorySubtree=true&groupBy=1'
    );
  });

  it('3. accepts local Date objects and normalizes them as calendar dates', () => {
    const query = buildProfitabilityQuery({
      preset: PROFITABILITY_PRESET.Custom,
      fromDate: new Date(2026, 10, 1),
      toDate: new Date(2026, 10, 10),
      groupBy: PROFITABILITY_GROUP_BY.Day,
    });

    assert.match(query, /fromDate=2026-11-01/);
    assert.match(query, /toDate=2026-11-10/);
  });

  it('4. maps sale types by name and ignores unknown values', () => {
    assert.match(
      buildProfitabilityQuery({ saleType: PROFITABILITY_SALE_TYPE.Merchandise }),
      /saleType=0/
    );
    assert.doesNotMatch(buildProfitabilityQuery({ saleType: 'Unknown' }), /saleType=/);
  });

  it('5. appends the export format last', () => {
    assert.strictEqual(
      buildProfitabilityQuery({ preset: PROFITABILITY_PRESET.Today }, 'pdf'),
      'preset=0&includeCategorySubtree=true&groupBy=1&format=pdf'
    );
  });
});

describe('profitabilityApi — summary, breakdown and export', () => {
  let originalFetch;
  let fetchCalls;

  beforeEach(() => {
    originalFetch = global.fetch;
    fetchCalls = [];
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  it('6. GETs the summary with the built query and forwards the abort signal', async () => {
    const payload = { reportCurrencyCode: 'USD', accrued: { netProfit: 10 } };
    let observedSignal = null;
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      observedSignal = config.signal;
      return jsonResponse(payload);
    };

    const controller = new AbortController();
    const result = await getProfitabilitySummary(
      { preset: PROFITABILITY_PRESET.Today },
      controller.signal
    );

    assert.strictEqual(fetchCalls.length, 1);
    assert.strictEqual(
      fetchCalls[0].url,
      'http://localhost:5000/api/profitability/summary?preset=0&includeCategorySubtree=true&groupBy=1'
    );
    assert.strictEqual(fetchCalls[0].config.method, 'GET');
    assert.ok(observedSignal);
    assert.deepStrictEqual(result, payload);
  });

  it('7. GETs the breakdown endpoint with the same filter contract', async () => {
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return jsonResponse({ series: [], paymentMethods: [] });
    };

    await getProfitabilityBreakdown({ categoryIds: [4], groupBy: 'Month' });

    assert.strictEqual(
      fetchCalls[0].url,
      'http://localhost:5000/api/profitability/breakdown?preset=0&categoryIds=4&includeCategorySubtree=true&groupBy=3'
    );
  });

  it('8. exports a blob with the filename and content type resolved from headers', async () => {
    const blob = { size: 42, type: 'text/csv' };
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return blobResponse(blob, {
        contentType: 'text/csv; charset=utf-8',
        contentDisposition:
          'attachment; filename="Estado_Resultados_20261101_20261110.csv"',
      });
    };

    const file = await exportProfitability({ preset: 'Custom' }, 'csv');

    assert.match(fetchCalls[0].url, /\/api\/profitability\/export\?/);
    assert.match(fetchCalls[0].url, /format=csv$/);
    assert.strictEqual(fetchCalls[0].config.method, 'GET');
    assert.strictEqual(fetchCalls[0].config.credentials, 'include');
    assert.strictEqual(file.blob, blob);
    assert.strictEqual(file.fileName, 'Estado_Resultados_20261101_20261110.csv');
    assert.strictEqual(file.contentType, 'text/csv');
  });

  it('9. decodes RFC 5987 filename* and falls back when the header is absent', async () => {
    assert.strictEqual(
      resolveExportFileName(
        "attachment; filename*=UTF-8''Estado_Resultados_20261101_20261110.pdf",
        'pdf'
      ),
      'Estado_Resultados_20261101_20261110.pdf'
    );
    assert.match(resolveExportFileName(null, 'xlsx'), /^Estado_Resultados_\d{8}\.xlsx$/);
  });

  it('10. surfaces the server ProblemDetails message on export failure', async () => {
    global.fetch = async () => ({
      ok: false,
      status: 400,
      statusText: 'Bad Request',
      headers: { get: () => 'application/problem+json' },
      text: async () =>
        JSON.stringify({ message: 'Formato de exportación inválido. Formatos válidos: csv, xlsx, pdf.' }),
    });

    await assert.rejects(
      () => exportProfitability({}, 'csv'),
      /Formato de exportación inválido/
    );
  });

  it('11. rejects unsupported export formats before touching the network', async () => {
    global.fetch = async () => {
      throw new Error('fetch must not be called');
    };

    await assert.rejects(() => exportProfitability({}, 'docx'), /Formato de exportación inválido/);
  });
});

describe('profitabilityApi — payment method fee rules (Admin-only endpoints)', () => {
  let originalFetch;
  let fetchCalls;

  beforeEach(() => {
    originalFetch = global.fetch;
    fetchCalls = [];
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  it('12. lists rules with the optional payment method and active filters', async () => {
    const rules = [{ id: 1, paymentMethodId: 4, feeType: 0 }];
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return jsonResponse(rules);
    };

    const result = await getFeeRules(4, true);

    assert.strictEqual(
      fetchCalls[0].url,
      'http://localhost:5000/api/payment-method-fees?paymentMethodId=4&activeOnly=true'
    );
    assert.strictEqual(fetchCalls[0].config.method, 'GET');
    assert.deepStrictEqual(result, rules);
  });

  it('13. lists every rule when no filter is supplied', async () => {
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return jsonResponse([]);
    };

    await getFeeRules();

    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/payment-method-fees');
  });

  it('14. POSTs a new rule as JSON', async () => {
    const request = {
      paymentMethodId: 4,
      feeType: 2,
      percentage: 1.5,
      fixedAmount: 0.3,
      currencyCode: 'USD',
      effectiveFromUtc: '2026-11-01T00:00:00.000Z',
      effectiveToUtc: null,
      isActive: true,
    };
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return jsonResponse({ id: 8, ...request }, 201);
    };

    const created = await createFeeRule(request);

    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/payment-method-fees');
    assert.strictEqual(fetchCalls[0].config.method, 'POST');
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), request);
    assert.strictEqual(created.id, 8);
  });

  it('15. PUTs an existing rule without a payment method id', async () => {
    const request = {
      feeType: 1,
      percentage: null,
      fixedAmount: 0.25,
      currencyCode: 'VES',
      effectiveFromUtc: '2026-11-01T00:00:00.000Z',
      effectiveToUtc: null,
      isActive: true,
    };
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return jsonResponse({ id: 8, ...request });
    };

    await updateFeeRule(8, request);

    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/payment-method-fees/8');
    assert.strictEqual(fetchCalls[0].config.method, 'PUT');
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), request);
  });

  it('16. DELETEs a rule and accepts the 204 no-content response', async () => {
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return noContentResponse();
    };

    const result = await deleteFeeRule(8);

    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/payment-method-fees/8');
    assert.strictEqual(fetchCalls[0].config.method, 'DELETE');
    assert.strictEqual(result, null);
  });
});
