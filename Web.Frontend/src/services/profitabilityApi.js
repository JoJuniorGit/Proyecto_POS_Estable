// F4-WU2 (Paso 14): cliente web del Estado de Resultados y de las reglas de comisión.
// La lectura usa el contrato Admin/Manager de /api/profitability; el CRUD de reglas usa el
// contrato Admin-only de /api/payment-method-fees. La comisión es costo del negocio y nunca
// se agrega al cliente.
import { ApiError, api, getBaseUrl } from './api';

export const PROFITABILITY_PRESET = Object.freeze({
  Today: 0,
  Yesterday: 1,
  Week: 2,
  Month: 3,
  Custom: 4,
});

export const PROFITABILITY_GROUP_BY = Object.freeze({
  Hour: 0,
  Day: 1,
  Week: 2,
  Month: 3,
});

export const PROFITABILITY_SALE_TYPE = Object.freeze({
  Merchandise: 0,
  CashAdvance: 1,
  Service: 2,
});

export const PROFITABILITY_VIEW_MODE = Object.freeze({
  Accrued: 'accrued',
  Realized: 'realized',
  Both: 'both',
});

export const PAYMENT_METHOD_FEE_TYPE = Object.freeze({
  Percentage: 0,
  Fixed: 1,
  PercentagePlusFixed: 2,
});

const EXPORT_FORMATS = Object.freeze(['csv', 'xlsx', 'pdf']);
const EXPORT_CONTENT_TYPES = Object.freeze({
  csv: 'text/csv',
  xlsx: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  pdf: 'application/pdf',
});

function normalizeEnumValue(value, mapping, fallback) {
  const values = Object.values(mapping);
  if (typeof value === 'number' && Number.isFinite(value)) {
    return values.includes(value) ? value : fallback;
  }

  if (typeof value === 'string') {
    const key = value.trim().toLowerCase();
    const match = Object.entries(mapping).find(([name]) => name.toLowerCase() === key);
    if (match) return match[1];
    const numeric = Number(value);
    if (Number.isInteger(numeric) && values.includes(numeric)) return numeric;
  }

  return fallback;
}

function resolvePreset(value) {
  return normalizeEnumValue(value, PROFITABILITY_PRESET, PROFITABILITY_PRESET.Today);
}

function resolveGroupBy(value) {
  return normalizeEnumValue(value, PROFITABILITY_GROUP_BY, PROFITABILITY_GROUP_BY.Day);
}

function resolveSaleType(value) {
  if (value === null || value === undefined || value === '') return null;
  const normalized = normalizeEnumValue(value, PROFITABILITY_SALE_TYPE, null);
  return normalized === null ? null : normalized;
}

/**
 * Converts a Date/ISO string to the local calendar date expected by the API (`yyyy-MM-dd`).
 * A plain date string is preserved verbatim to avoid timezone shifts.
 */
export function formatProfitabilityDate(value) {
  if (value === null || value === undefined || value === '') return null;
  if (typeof value === 'string') {
    const direct = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value.trim());
    if (direct) return `${direct[1]}-${direct[2]}-${direct[3]}`;
  }

  const parsed = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(parsed.getTime())) return null;
  const year = parsed.getFullYear();
  const month = String(parsed.getMonth() + 1).padStart(2, '0');
  const day = String(parsed.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

function appendIdList(parts, name, values) {
  if (!Array.isArray(values)) return;
  for (const value of values) {
    const numeric = Number(value);
    if (Number.isInteger(numeric) && numeric > 0) {
      parts.push(`${name}=${numeric}`);
    }
  }
}

/**
 * Builds the query string shared by summary, breakdown and export. The order mirrors the WPF
 * client so both clients hit the same contract deterministically.
 */
export function buildProfitabilityQuery(filters = {}, format = null) {
  const source = filters && typeof filters === 'object' ? filters : {};
  const parts = [`preset=${resolvePreset(source.preset)}`];

  const fromDate = formatProfitabilityDate(source.fromDate);
  if (fromDate) parts.push(`fromDate=${fromDate}`);

  const toDate = formatProfitabilityDate(source.toDate);
  if (toDate) parts.push(`toDate=${toDate}`);

  appendIdList(parts, 'categoryIds', source.categoryIds);
  parts.push(`includeCategorySubtree=${source.includeCategorySubtree === false ? 'false' : 'true'}`);
  appendIdList(parts, 'productIds', source.productIds);
  appendIdList(parts, 'paymentMethodIds', source.paymentMethodIds);

  const cashierId = Number(source.cashierId);
  if (Number.isInteger(cashierId) && cashierId > 0) {
    parts.push(`cashierId=${cashierId}`);
  }

  const saleType = resolveSaleType(source.saleType);
  if (saleType !== null) {
    parts.push(`saleType=${saleType}`);
  }

  if (typeof source.priceListType === 'string' && source.priceListType.trim()) {
    parts.push(`priceListType=${encodeURIComponent(source.priceListType.trim())}`);
  }

  parts.push(`groupBy=${resolveGroupBy(source.groupBy)}`);

  if (typeof format === 'string' && format.trim()) {
    parts.push(`format=${encodeURIComponent(format.trim().toLowerCase())}`);
  }

  return parts.join('&');
}

export function getProfitabilitySummary(filters = {}, signal) {
  return api.get(`/api/profitability/summary?${buildProfitabilityQuery(filters)}`, signal);
}

export function getProfitabilityBreakdown(filters = {}, signal) {
  return api.get(`/api/profitability/breakdown?${buildProfitabilityQuery(filters)}`, signal);
}

function resolveFileNameFromDisposition(disposition, format) {
  if (typeof disposition === 'string' && disposition) {
    const encoded = /filename\*=UTF-8''([^;]+)/i.exec(disposition);
    if (encoded?.[1]) {
      try {
        return decodeURIComponent(encoded[1].trim());
      } catch {
        // Fall through to the plain filename form.
      }
    }

    const plain = /filename="?([^";]+)"?/i.exec(disposition);
    if (plain?.[1]) {
      return plain[1].trim();
    }
  }

  const now = new Date();
  const stamp = `${now.getFullYear()}${String(now.getMonth() + 1).padStart(2, '0')}${String(now.getDate()).padStart(2, '0')}`;
  return `Estado_Resultados_${stamp}.${format}`;
}

export function resolveExportFileName(contentDisposition, format = 'csv') {
  return resolveFileNameFromDisposition(contentDisposition, format);
}

function normalizeExportFormat(format) {
  const normalized = String(format ?? '').trim().toLowerCase();
  if (!EXPORT_FORMATS.includes(normalized)) {
    throw new Error('Formato de exportación inválido. Formatos válidos: csv, xlsx, pdf.');
  }
  return normalized;
}

function createExportSignal(signal) {
  if (typeof AbortSignal === 'undefined' || typeof AbortSignal.timeout !== 'function') {
    return signal;
  }

  const timeout = AbortSignal.timeout(60000);
  if (signal && typeof AbortSignal.any === 'function') {
    return AbortSignal.any([signal, timeout]);
  }

  return signal || timeout;
}

async function readExportError(response) {
  let message = `Error ${response.status}: ${response.statusText}`;
  let body = null;

  try {
    const raw = await response.text();
    if (raw) {
      try {
        const json = JSON.parse(raw);
        body = json;
        message = json.message
          || json.Message
          || json.detail
          || json.Detail
          || json.title
          || json.Title
          || message;
      } catch {
        if (!/[<>]/.test(raw) && raw.length < 300) {
          message = raw;
        }
      }
    }
  } catch {
    // Keep the status-based message when the body cannot be read.
  }

  if (response.status === 401) {
    try {
      localStorage.removeItem('pos_user');
      localStorage.removeItem('pos_token');
      sessionStorage.clear();
      if (typeof window !== 'undefined') {
        window.dispatchEvent(new CustomEvent('pos_unauthorized'));
      }
    } catch {
      // The caller still receives the ApiError below.
    }
  }

  return new ApiError(message, response.status, body);
}

/**
 * Downloads the server-side Estado de Resultados. Returns the blob plus the filename and
 * content type advertised by the API; the page decides how to save it.
 */
export async function exportProfitability(filters = {}, format = 'csv', signal) {
  const normalizedFormat = normalizeExportFormat(format);
  const query = buildProfitabilityQuery(filters, normalizedFormat);
  const response = await fetch(`${getBaseUrl()}/api/profitability/export?${query}`, {
    method: 'GET',
    credentials: 'include',
    headers: {
      Accept: '*/*',
      'X-Client-Platform': 'Web',
      'X-Client-Version': '1.0.0',
    },
    signal: createExportSignal(signal),
  });

  if (!response.ok) {
    throw await readExportError(response);
  }

  const blob = await response.blob();
  const contentTypeHeader = response.headers.get('content-type') || '';
  const contentType = contentTypeHeader.split(';')[0].trim()
    || EXPORT_CONTENT_TYPES[normalizedFormat];
  const fileName = resolveExportFileName(
    response.headers.get('content-disposition'),
    normalizedFormat
  );

  return { blob, fileName, contentType };
}

/**
 * Triggers the browser download for an exported file. Returns false in SSR/test environments
 * without DOM, so callers can render a neutral status instead of throwing.
 */
export function saveProfitabilityExport(file) {
  if (
    !file
    || !file.blob
    || typeof document === 'undefined'
    || typeof URL === 'undefined'
    || typeof URL.createObjectURL !== 'function'
  ) {
    return false;
  }

  const objectUrl = URL.createObjectURL(file.blob);
  try {
    const anchor = document.createElement('a');
    anchor.href = objectUrl;
    anchor.download = file.fileName || 'Estado_Resultados.csv';
    anchor.rel = 'noopener';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    return true;
  } finally {
    setTimeout(() => {
      try {
        URL.revokeObjectURL(objectUrl);
      } catch {
        // Revoking a already-released URL is harmless.
      }
    }, 30000);
  }
}

function assertFeeRuleId(id) {
  const numeric = Number(id);
  if (!Number.isInteger(numeric) || numeric <= 0) {
    throw new Error('La regla de comisión debe tener un identificador válido.');
  }
  return numeric;
}

export function getFeeRules(paymentMethodId = null, activeOnly = null, signal) {
  const parts = [];

  const methodId = Number(paymentMethodId);
  if (Number.isInteger(methodId) && methodId > 0) {
    parts.push(`paymentMethodId=${methodId}`);
  }

  if (typeof activeOnly === 'boolean') {
    parts.push(`activeOnly=${activeOnly ? 'true' : 'false'}`);
  }

  const query = parts.length > 0 ? `?${parts.join('&')}` : '';
  return api.get(`/api/payment-method-fees${query}`, signal);
}

export function createFeeRule(payload, signal) {
  return api.post('/api/payment-method-fees', payload, signal);
}

export function updateFeeRule(id, payload, signal) {
  return api.put(`/api/payment-method-fees/${assertFeeRuleId(id)}`, payload, signal);
}

export function deleteFeeRule(id, signal) {
  return api.delete(`/api/payment-method-fees/${assertFeeRuleId(id)}`, signal);
}
