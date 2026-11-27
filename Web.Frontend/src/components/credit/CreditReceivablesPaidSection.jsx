import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  AlertTriangle,
  Calendar,
  CheckCircle2,
  ChevronLeft,
  ChevronRight,
  Download,
  FileText,
  Loader2,
  Search,
  Wallet,
  X,
} from 'lucide-react';
import { getActivePaymentMethods } from '../../services/paymentApi';
import {
  getPaidReceivablePayments,
  getPaidReceivables,
  getReceivablePaymentTimeline,
} from '../../services/creditApi';
import { useMonetaryProfile } from '../../context/MonetaryProfileContext';
import { displayCurrencyCode } from '../../utils/monetaryProfile';
import { resolveLocalCurrencyProfile } from '../register/registerFormLogic';
import { formatDateVe } from './creditLogic';
import { formatNumberEs } from '../../utils/formatters';
import './credit-paid.css';

const EMPTY_SUMMARY = {
  paidDebtsCount: 0,
  totalCollectedUsd: 0,
  totalCollectedLocalHistorical: 0,
  totalPaymentsCount: 0,
  averageDaysToSettle: 0,
};

const EMPTY_FILTERS = { search: '', from: '', to: '', methodId: '' };

function toIsoDate(date) {
  if (!date) return '';
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

function presetRange(preset) {
  const today = new Date();
  if (preset === 'Hoy') return { from: toIsoDate(today), to: toIsoDate(today) };
  if (preset === 'Semana') {
    const diff = (today.getDay() + 6) % 7;
    const monday = new Date(today);
    monday.setDate(today.getDate() - diff);
    return { from: toIsoDate(monday), to: toIsoDate(today) };
  }
  if (preset === 'Mes') {
    const first = new Date(today.getFullYear(), today.getMonth(), 1);
    return { from: toIsoDate(first), to: toIsoDate(today) };
  }
  return { from: '', to: '' };
}

function invoiceLabel(row) {
  return row?.invoiceNumber ? `F${String(row.invoiceNumber).padStart(5, '0')}` : `V${row?.saleId ?? '—'}`;
}

function initials(name) {
  const parts = String(name || '').trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  return (parts.length === 1 ? parts[0].slice(0, 2) : `${parts[0][0]}${parts[1][0]}`).toUpperCase();
}

function formatDateTime(value) {
  if (!value) return '—';
  return new Date(value).toLocaleString('es-VE', {
    day: '2-digit',
    month: '2-digit',
    year: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function downloadCsv(filename, rows) {
  const content = rows.map((row) => row.map((cell) => {
    const text = String(cell ?? '');
    return /[;"\n]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
  }).join(';')).join('\n');
  const blob = new Blob([`\uFEFF${content}`], { type: 'text/csv;charset=utf-8;' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  anchor.click();
  URL.revokeObjectURL(url);
}

function downloadExcel(filename, rows) {
  const html = `<table>${rows.map((row) => `<tr>${row.map((cell) => `<td>${String(cell ?? '')}</td>`).join('')}</tr>`).join('')}</table>`;
  const blob = new Blob([html], { type: 'application/vnd.ms-excel;charset=utf-8;' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  anchor.click();
  URL.revokeObjectURL(url);
}

function printHtml(title, bodyHtml) {
  const win = window.open('', '_blank', 'width=900,height=700');
  if (!win) return;
  win.document.write(`<html><head><title>${title}</title><style>body{font-family:Segoe UI,Arial,sans-serif;padding:24px;color:#0F172A}h1{font-size:20px}table{width:100%;border-collapse:collapse;font-size:12px}th,td{border-bottom:1px solid #E2E8F0;padding:8px;text-align:left}th{background:#F8FAFC;color:#64748B}</style></head><body>${bodyHtml}</body></html>`);
  win.document.close();
  win.focus();
  win.print();
}

export default function CreditReceivablesPaidSection({ refreshKey = 0, onCountChange }) {
  const { profile, primaryCurrencyCode, formatPrimary, formatSecondary } = useMonetaryProfile();
  const primaryLabel = displayCurrencyCode(primaryCurrencyCode) || 'USD';
  const { isDual } = resolveLocalCurrencyProfile(profile);

  const [items, setItems] = useState([]);
  const [summary, setSummary] = useState(EMPTY_SUMMARY);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [draft, setDraft] = useState(EMPTY_FILTERS);
  const [filters, setFilters] = useState(EMPTY_FILTERS);
  const [methods, setMethods] = useState([]);
  const [mode, setMode] = useState('invoice');
  const [page, setPage] = useState(1);
  const [total, setTotal] = useState(0);
  const [hasMore, setHasMore] = useState(false);
  const pageSize = 25;

  const [timelineOpen, setTimelineOpen] = useState(false);
  const [timeline, setTimeline] = useState(null);
  const [timelineLoading, setTimelineLoading] = useState(false);
  const [timelineError, setTimelineError] = useState(null);
  const [timelineIds, setTimelineIds] = useState([]);
  const [timelineIndex, setTimelineIndex] = useState(-1);

  useEffect(() => {
    let cancelled = false;
    getActivePaymentMethods()
      .then((result) => { if (!cancelled) setMethods(result || []); })
      .catch(() => { if (!cancelled) setMethods([]); });
    return () => { cancelled = true; };
  }, []);

  const load = useCallback(async (signal) => {
    setLoading(true);
    setError(null);
    try {
      const filter = {
        search: filters.search,
        paidFrom: filters.from,
        paidTo: filters.to,
        paymentMethodId: filters.methodId,
        page,
        pageSize,
      };
      const result = mode === 'invoice'
        ? await getPaidReceivables(filter, signal)
        : await getPaidReceivablePayments(filter, signal);
      setItems(result?.items || []);
      setSummary(result?.summary || EMPTY_SUMMARY);
      setTotal(Number(result?.totalCount || 0));
      setHasMore(Boolean(result?.hasMore));
      onCountChange?.(Number(result?.totalCount || 0));
    } catch (err) {
      if (err?.name === 'AbortError') return;
      setError(err?.message || 'No se pudo cargar el historial de deudas pagadas.');
      setItems([]);
      setSummary(EMPTY_SUMMARY);
      setTotal(0);
      setHasMore(false);
    } finally {
      setLoading(false);
    }
  }, [filters, mode, page, onCountChange]);

  useEffect(() => {
    const controller = new AbortController();
    load(controller.signal);
    return () => controller.abort();
  }, [load, refreshKey]);

  useEffect(() => {
    if (!timelineOpen) return undefined;
    const onKeyDown = (event) => {
      if (event.key === 'Escape') {
        setTimelineOpen(false);
        setTimeline(null);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [timelineOpen]);

  const applyFilters = () => {
    setPage(1);
    setFilters({ ...draft });
  };

  const clearFilters = () => {
    setDraft(EMPTY_FILTERS);
    setFilters(EMPTY_FILTERS);
    setPage(1);
  };

  const applyPreset = (preset) => {
    const range = presetRange(preset);
    const next = { ...draft, ...range };
    setDraft(next);
    setFilters(next);
    setPage(1);
  };

  const changeMode = (nextMode) => {
    setMode(nextMode);
    setPage(1);
  };

  const loadTimeline = async (obligationId) => {
    setTimelineLoading(true);
    setTimelineError(null);
    setTimelineOpen(true);
    try {
      setTimeline(await getReceivablePaymentTimeline(obligationId));
    } catch (err) {
      setTimelineError(err?.message || 'No se pudo cargar la cronología de abonos.');
      setTimeline(null);
    } finally {
      setTimelineLoading(false);
    }
  };

  const openTimeline = async (row) => {
    const id = row?.obligationId ?? row?.allocations?.[0]?.obligationId;
    if (!id) return;
    const ids = items
      .map((item) => item?.obligationId ?? item?.allocations?.[0]?.obligationId)
      .filter(Boolean);
    setTimelineIds(ids);
    setTimelineIndex(ids.indexOf(id));
    await loadTimeline(id);
  };

  const moveTimeline = async (delta) => {
    const next = timelineIndex + delta;
    if (next < 0 || next >= timelineIds.length) return;
    setTimelineIndex(next);
    await loadTimeline(timelineIds[next]);
  };

  const exportRows = useMemo(() => {
    if (mode === 'payment') {
      return [
        ['Fecha', 'Cajero', 'Cliente', 'Documento', 'Facturas', 'Método', 'Moneda', 'Monto cobrado', 'Tasa', 'Acreditado USD', 'Referencia', 'Imputación'],
        ...items.map((row) => [
          formatDateTime(row.paidAt),
          row.cashierName,
          row.customerName,
          row.customerDocument,
          row.invoiceSummary,
          row.paymentMethodName,
          row.paymentCurrency,
          row.amountPaidInCurrency,
          row.exchangeRateApplied,
          row.amountCreditedUsd,
          row.reference || '',
          row.imputationMode,
        ]),
      ];
    }
    return [
      ['Documento', 'Cliente', 'Documento cliente', 'Emisión', 'Pagada', 'Días', 'A tiempo', 'Total pagado USD', 'Pagado Bs.S histórico', 'Abonos', 'Métodos'],
      ...items.map((row) => [
        invoiceLabel(row),
        row.customerName,
        row.customerDocument,
        formatDateVe(row.issuedAt),
        formatDateVe(row.paidAt),
        row.daysToSettle,
        row.settledOnTime ? 'A tiempo' : 'Tras mora',
        row.totalPaidUsd,
        row.totalPaidLocalHistorical,
        row.paymentsCount,
        row.paymentMethodsSummary,
      ]),
    ];
  }, [items, mode]);

  const handleExport = (format) => {
    const stamp = new Date().toISOString().slice(0, 10);
    if (format === 'csv') {
      downloadCsv(`deudas-pagadas-${stamp}.csv`, exportRows);
      return;
    }
    if (format === 'xlsx') {
      downloadExcel(`deudas-pagadas-${stamp}.xls`, exportRows);
      return;
    }
    printHtml('Deudas pagadas', `<h1>Deudas pagadas</h1><table>${exportRows.map((row, index) => `<tr>${row.map((cell) => `<${index === 0 ? 'th' : 'td'}>${String(cell ?? '')}</${index === 0 ? 'th' : 'td'}>`).join('')}</tr>`).join('')}</table>`);
  };

  const printCancellation = () => {
    if (!timeline) return;
    const rows = (timeline.payments || []).map((payment) => `<tr><td>${formatDateTime(payment.paidAt)}</td><td>${payment.paymentMethodName}</td><td>${payment.reference || '—'}</td><td>${formatNumberEs(payment.amountPaidInCurrency)} ${payment.paymentCurrency}</td><td>${formatNumberEs(payment.exchangeRateApplied)}</td><td>${formatNumberEs(payment.amountCreditedUsd)} USD</td></tr>`).join('');
    printHtml(
      `Constancia ${invoiceLabel(timeline)}`,
      `<h1>Constancia de cancelación</h1><p><strong>Factura:</strong> ${invoiceLabel(timeline)} · <strong>Cliente:</strong> ${timeline.customerName} (${timeline.customerDocument})</p><p><strong>Original:</strong> ${formatPrimary(timeline.originalAmountUsd)} · <strong>Total abonado:</strong> ${formatPrimary(timeline.totalPaidUsd)} · <strong>Saldo final:</strong> ${formatPrimary(timeline.balanceUsd)}</p><table><tr><th>Fecha</th><th>Método</th><th>Referencia</th><th>Monto</th><th>Tasa</th><th>Acreditado</th></tr>${rows}</table>`
    );
  };

  const totalPages = Math.max(1, Math.ceil(total / pageSize));

  return (
    <div className="credit-paid">
      <section className="credit-paid-toolbar">
        <div className="credit-paid-filters">
          <div className="credit-paid-search">
            <Search size={18} />
            <input
              type="text"
              className="input-field"
              placeholder="Cliente, cédula/RIF, N° factura o referencia..."
              value={draft.search}
              onChange={(event) => setDraft((current) => ({ ...current, search: event.target.value }))}
              aria-label="Buscar en deudas pagadas"
            />
            {draft.search && (
              <button type="button" className="credit-paid-clear" onClick={() => setDraft((current) => ({ ...current, search: '' }))} aria-label="Limpiar búsqueda">
                <X size={14} />
              </button>
            )}
          </div>

          <label className="credit-paid-date">
            <Calendar size={15} />
            <span>Desde</span>
            <input type="date" value={draft.from} onChange={(event) => setDraft((current) => ({ ...current, from: event.target.value }))} />
          </label>

          <label className="credit-paid-date">
            <Calendar size={15} />
            <span>Hasta</span>
            <input type="date" value={draft.to} onChange={(event) => setDraft((current) => ({ ...current, to: event.target.value }))} />
          </label>

          <select
            className="form-select credit-paid-method"
            value={draft.methodId}
            onChange={(event) => setDraft((current) => ({ ...current, methodId: event.target.value }))}
            aria-label="Método de pago"
          >
            <option value="">Todos los métodos</option>
            {methods.map((method) => (
              <option key={method.id} value={method.id}>{method.name}</option>
            ))}
          </select>

          <button type="button" className="btn btn-primary" onClick={applyFilters}>Aplicar</button>
          <button type="button" className="btn btn-outline" onClick={clearFilters}>Limpiar</button>
        </div>

        <div className="credit-paid-second-row">
          <div className="credit-paid-presets">
            {['Hoy', 'Esta Semana', 'Mes Actual', 'Todo'].map((preset) => (
              <button key={preset} type="button" className="credit-paid-chip" onClick={() => applyPreset(preset)}>
                {preset}
              </button>
            ))}
            <div className="credit-paid-mode" role="group" aria-label="Vista del historial">
              <button type="button" className={mode === 'invoice' ? 'is-active' : ''} onClick={() => changeMode('invoice')}>Por factura</button>
              <button type="button" className={mode === 'payment' ? 'is-active' : ''} onClick={() => changeMode('payment')}>Por abono</button>
            </div>
          </div>

          <div className="credit-paid-exports">
            <button type="button" className="credit-paid-export" onClick={() => handleExport('csv')}><Download size={14} /> CSV</button>
            <button type="button" className="credit-paid-export credit-paid-export--xlsx" onClick={() => handleExport('xlsx')}><Download size={14} /> Excel</button>
            <button type="button" className="credit-paid-export credit-paid-export--pdf" onClick={() => handleExport('pdf')}><FileText size={14} /> PDF</button>
          </div>
        </div>
      </section>

      <section className="credit-paid-kpis">
        <article className="credit-paid-kpi credit-paid-kpi--green">
          <span>DEUDAS PAGADAS</span>
          <strong>{formatNumberEs(summary.paidDebtsCount || 0, 0)}</strong>
          <small>✓ 100% saldadas</small>
        </article>
        <article className="credit-paid-kpi credit-paid-kpi--purple">
          <span>COBRADO REAL ({primaryLabel})</span>
          <strong>{formatPrimary(summary.totalCollectedUsd || 0)}</strong>
          {isDual && <small>{formatSecondary(summary.totalCollectedLocalHistorical || 0)} histórico</small>}
        </article>
        <article className="credit-paid-kpi credit-paid-kpi--blue">
          <span>ABONOS REGISTRADOS</span>
          <strong>{formatNumberEs(summary.totalPaymentsCount || 0, 0)}</strong>
          <small>Pagos/cuotas aplicados</small>
        </article>
        <article className="credit-paid-kpi credit-paid-kpi--slate">
          <span>TIEMPO PROM. DE PAGO</span>
          <strong>{formatNumberEs(summary.averageDaysToSettle || 0, 1)} días</strong>
          <small>Emisión → cancelación</small>
        </article>
      </section>

      <section className="credit-paid-table-card">
        {loading ? (
          <div className="credit-paid-state" role="status"><Loader2 size={28} className="animate-spin" /><p>Cargando historial...</p></div>
        ) : error ? (
          <div className="credit-paid-state credit-paid-state--error" role="alert"><AlertTriangle size={24} /><p>{error}</p><button type="button" className="btn btn-outline" onClick={applyFilters}>Reintentar</button></div>
        ) : items.length === 0 ? (
          <div className="credit-paid-state"><CheckCircle2 size={28} /><p>No hay deudas pagadas para los filtros seleccionados.</p></div>
        ) : mode === 'invoice' ? (
          <div className="credit-paid-table-wrap">
            <table className="credit-paid-table">
              <thead>
                <tr>
                  <th>DOCUMENTO</th>
                  <th>CLIENTE</th>
                  <th>EMISIÓN / PAGADA</th>
                  <th className="text-center">ABONOS</th>
                  <th className="text-right">TOTAL SALDADO</th>
                  <th className="text-center">ESTADO</th>
                  <th className="text-right">ACCIONES</th>
                </tr>
              </thead>
              <tbody>
                {items.map((row) => (
                  <tr key={row.obligationId}>
                    <td><strong>{invoiceLabel(row)}</strong><span className="credit-paid-sub">Venta #{row.saleId}</span></td>
                    <td>
                      <div className="credit-paid-customer">
                        <span className="credit-paid-avatar">{initials(row.customerName)}</span>
                        <div><strong>{row.customerName}</strong><span>{row.customerDocument}</span></div>
                      </div>
                    </td>
                    <td><strong className="credit-paid-paid-date">Pagada: {formatDateVe(row.paidAt)}</strong><span className="credit-paid-sub">Emisión: {formatDateVe(row.issuedAt)}</span></td>
                    <td className="text-center"><span className="credit-paid-pill">{row.paymentsCount} {row.paymentsCount === 1 ? 'abono' : 'abonos'}</span><span className="credit-paid-sub">{row.paymentMethodsSummary}</span></td>
                    <td className="text-right"><strong>{formatPrimary(row.totalPaidUsd)}</strong>{isDual && <span className="credit-paid-sub">{formatSecondary(row.totalPaidLocalHistorical)}</span>}</td>
                    <td className="text-center"><span className="credit-paid-status">● Pagada</span><span className="credit-paid-sub">{row.settledOnTime ? 'A tiempo' : 'Tras mora'}</span></td>
                    <td className="text-right">
                      <div className="credit-paid-actions">
                        <button type="button" className="credit-paid-soft-btn" onClick={() => openTimeline(row)}>Ver abonos</button>
                        <button type="button" className="btn btn-outline btn-sm" onClick={() => openTimeline(row)}>PDF</button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <div className="credit-paid-table-wrap">
            <table className="credit-paid-table">
              <thead>
                <tr>
                  <th>FECHA / CAJERO</th>
                  <th>CLIENTE</th>
                  <th>FACTURA(S)</th>
                  <th>MÉTODO / REF.</th>
                  <th className="text-right">MONTO COBRADO</th>
                  {isDual && <th className="text-right">ACREDITADO</th>}
                  <th className="text-right">ACCIONES</th>
                </tr>
              </thead>
              <tbody>
                {items.map((row) => (
                  <tr key={row.paymentId}>
                    <td><strong>{formatDateTime(row.paidAt)}</strong><span className="credit-paid-sub">{row.cashierName}</span></td>
                    <td>
                      <div className="credit-paid-customer">
                        <span className="credit-paid-avatar">{initials(row.customerName)}</span>
                        <div><strong>{row.customerName}</strong><span>{row.customerDocument}</span></div>
                      </div>
                    </td>
                    <td><strong>{row.invoiceSummary}</strong><span className="credit-paid-tag">{row.imputationMode}</span></td>
                    <td><strong>{row.paymentMethodName}</strong>{row.reference && <span className="credit-paid-sub">Ref: {row.reference}</span>}</td>
                    <td className="text-right"><strong>{row.paymentCurrency === 'USD' ? formatPrimary(row.amountPaidInCurrency) : formatSecondary(row.amountPaidInCurrency)}</strong>{isDual && <span className="credit-paid-sub">Tasa: {formatNumberEs(row.exchangeRateApplied)}</span>}</td>
                    {isDual && <td className="text-right credit-paid-credited">{formatPrimary(row.amountCreditedUsd)}</td>}
                    <td className="text-right"><button type="button" className="credit-paid-soft-btn" onClick={() => openTimeline(row)}>Detalle</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        <footer className="credit-paid-pagination">
          <span>Página {Math.min(page, totalPages)} de {totalPages} · {total} deudas</span>
          <div>
            <button type="button" className="btn btn-outline btn-sm" disabled={page <= 1 || loading} onClick={() => setPage((current) => Math.max(1, current - 1))}><ChevronLeft size={14} /> Anterior</button>
            <button type="button" className="btn btn-outline btn-sm" disabled={!hasMore || loading} onClick={() => setPage((current) => current + 1)}>Siguiente <ChevronRight size={14} /></button>
          </div>
        </footer>
      </section>

      {timelineOpen && (
        <div className="credit-paid-drawer-backdrop" role="presentation" onClick={() => { setTimelineOpen(false); setTimeline(null); }}>
          <aside className="credit-paid-drawer" role="dialog" aria-modal="true" aria-label="Historial de abonos" onClick={(event) => event.stopPropagation()}>
            <header className="credit-paid-drawer-head">
              <div className="credit-paid-drawer-title">
                <span className="credit-paid-drawer-badge"><Wallet size={18} /></span>
                <div>
                  <strong>Factura {timeline ? invoiceLabel(timeline) : '—'}</strong>
                  <span>{timeline?.customerName || '—'} · {timeline?.customerDocument || '—'}</span>
                </div>
              </div>
              <div className="credit-paid-drawer-actions">
                <span className={`credit-paid-status ${timeline?.balanceUsd <= 0.005 ? '' : 'is-partial'}`}>{timeline?.balanceUsd <= 0.005 ? '● Pagada' : `● ${timeline?.status || 'Parcial'}`}</span>
                <button type="button" className="btn btn-outline btn-sm" disabled={timelineIndex <= 0} onClick={() => moveTimeline(-1)} aria-label="Factura anterior"><ChevronLeft size={14} /></button>
                <button type="button" className="btn btn-outline btn-sm" disabled={timelineIndex < 0 || timelineIndex >= timelineIds.length - 1} onClick={() => moveTimeline(1)} aria-label="Factura siguiente"><ChevronRight size={14} /></button>
                <button type="button" className="btn btn-outline btn-sm" onClick={() => { setTimelineOpen(false); setTimeline(null); }} aria-label="Cerrar">×</button>
              </div>
            </header>

            <div className="credit-paid-drawer-body">
              {timelineLoading && <div className="credit-paid-state"><Loader2 size={24} className="animate-spin" /><p>Cargando cronología...</p></div>}
              {timelineError && <div className="credit-paid-alert credit-paid-alert--error"><AlertTriangle size={16} /> {timelineError}</div>}
              {timeline && !timelineLoading && (
                <>
                  <div className="credit-paid-summary-bar">
                    <div><span>MONTO ORIGINAL</span><strong>{formatPrimary(timeline.originalAmountUsd)}</strong></div>
                    <div><span>TOTAL ABONADO</span><strong>{formatPrimary(timeline.totalPaidUsd)}</strong></div>
                    <div><span>SALDO FINAL</span><strong className={timeline.balanceUsd <= 0.005 ? 'is-settled' : ''}>{formatPrimary(timeline.balanceUsd)}</strong></div>
                  </div>
                  <ol className="credit-paid-timeline">
                    {(timeline.payments || []).map((payment) => (
                      <li key={payment.paymentId} className={payment.runningBalanceAfterUsd <= 0.005 ? 'is-final' : ''}>
                        <span className="credit-paid-timeline-dot" />
                        <article className="credit-paid-timeline-card">
                          <header>
                            <strong>Abono #{payment.sequenceNumber}</strong>
                            <span>{formatDateTime(payment.paidAt)}</span>
                            <em>{payment.imputationMode}</em>
                          </header>
                          <p>{payment.paymentMethodName} · {payment.cashierName}{payment.reference ? ` · Ref: ${payment.reference}` : ''}</p>
                          <div className="credit-paid-timeline-money">
                            <div><strong>{payment.paymentCurrency === 'USD' ? formatPrimary(payment.amountPaidInCurrency) : formatSecondary(payment.amountPaidInCurrency)}</strong>{isDual && <span>Tasa aplicada: {formatNumberEs(payment.exchangeRateApplied)}</span>}</div>
                            {isDual && <div className="credit-paid-credited"><strong>{formatPrimary(payment.amountCreditedUsd)}</strong><span>acreditado</span></div>}
                          </div>
                          <footer>Saldo tras este abono: <strong>{formatPrimary(payment.runningBalanceAfterUsd)}</strong></footer>
                        </article>
                      </li>
                    ))}
                  </ol>
                </>
              )}
            </div>

            <footer className="credit-paid-drawer-foot">
              <button type="button" className="btn btn-outline" onClick={() => { setTimelineOpen(false); setTimeline(null); }}>Cerrar (Esc)</button>
              <button type="button" className="btn btn-primary" onClick={printCancellation} disabled={!timeline}>Descargar Constancia PDF</button>
            </footer>
          </aside>
        </div>
      )}
    </div>
  );
}

