import { useCallback, useEffect, useState } from 'react';
import RoleGuard from '../navigation/RoleGuard';
import { useAuth } from '../context/AuthContext';
import { normalizeRole } from '../navigation/roleViews';
import useDebounce from '../hooks/useDebounce';
import {
  cancelCreditSale,
  getCreditPaymentReceipt,
  getCreditReceivables,
  getCreditSaleReceipt,
} from '../services/creditApi';
import Modal from '../components/ui/Modal';
import CreditPaymentModal from '../components/credit/CreditPaymentModal';
import CreditStatementModal from '../components/credit/CreditStatementModal';
import CreditReturnModal from '../components/credit/CreditReturnModal';
import {
  canReturnReceivable,
  formatDateVe,
  hasOverdueAlert,
  isReceivableCancelable,
  isReceivableOpen,
  RECEIVABLE_STATUS_OPTIONS,
} from '../components/credit/creditLogic';
import {
  creditPaymentReceiptFileName,
  creditSaleReceiptFileName,
  saveReceiptBlob,
} from '../components/credit/receipts';
import { formatNumberEs } from '../utils/formatters';
import { useMonetaryProfile } from '../context/MonetaryProfileContext';
import { displayCurrencyCode } from '../utils/monetaryProfile';
import { resolveLocalCurrencyProfile } from '../components/register/registerFormLogic';
import { AlertTriangle, CheckCircle2, Download, HandCoins, Loader2, RefreshCw, Search, Trash2, Undo2, Wallet } from 'lucide-react';
import '../components/credit/credit.css';

/**
 * 8.211-T4b2b2b (S9): etiquetas del perfil monetario activo para cartera CxC. La deuda se
 * denomina en la primaria; las columnas locales y la tasa sólo se muestran en `Dual`.
 * @param {{ mode?: string, primaryCurrencyCode?: string, secondaryCurrencyCode?: string }} profile
 */
export function resolveCreditReceivablesCurrencyLabels(profile) {
  const local = resolveLocalCurrencyProfile(profile);
  return {
    isDual: local.isDual,
    primaryLabel: displayCurrencyCode(profile?.primaryCurrencyCode) || 'USD',
    localLabel: local.label || 'Bs.S',
  };
}

/**
 * 8.187-T5 (Paso 11/12, F4, S3/S5/S7): página de Cuentas por Cobrar del POS web (molde
 * PendingOrders). La cartera y los totales los calcula el servidor; la UI filtra, presenta las
 * alertas de mora y orquesta abonos, anulación, devolución y comprobantes. El abono ingresa a
 * la sesión de caja activa del lado del servidor (aquí solo se dispara el cobro).
 */
export default function CreditReceivablesPage() {
  return (
    <RoleGuard view="credit">
      <CreditReceivablesPageContent />
    </RoleGuard>
  );
}

function CreditReceivablesPageContent() {
  const { user } = useAuth();
  const { profile: monetaryProfile, formatPrimary, formatSecondary } = useMonetaryProfile();
  const { isDual, primaryLabel, localLabel } = resolveCreditReceivablesCurrencyLabels(monetaryProfile);
  const normalizedRole = normalizeRole(user?.role);
  const isElevated = normalizedRole === 'Admin' || normalizedRole === 'Manager';

  const [items, setItems] = useState([]);
  const [totals, setTotals] = useState({ totalOpenUSD: 0, totalOverdueUSD: 0, openCount: 0, todayRate: 0 });
  const [isLoading, setIsLoading] = useState(true);
  const [loadError, setLoadError] = useState(null);

  const [searchQuery, setSearchQuery] = useState('');
  const [status, setStatus] = useState('Todos');
  const [overdueOnly, setOverdueOnly] = useState(false);
  const debouncedSearch = useDebounce(searchQuery, 300);

  const [successMessage, setSuccessMessage] = useState(null);
  const [actionError, setActionError] = useState(null);

  const [paymentRow, setPaymentRow] = useState(null);
  const [statementRow, setStatementRow] = useState(null);
  const [returnRow, setReturnRow] = useState(null);
  const [cancelRow, setCancelRow] = useState(null);
  const [cancelReason, setCancelReason] = useState('');
  const [isCancelling, setIsCancelling] = useState(false);
  const [isDownloading, setIsDownloading] = useState(false);

  const loadList = useCallback(async () => {
    setIsLoading(true);
    setLoadError(null);
    try {
      const result = await getCreditReceivables({
        status: status === 'Todos' ? null : status,
        overdueOnly,
        search: debouncedSearch,
      });
      setItems(result?.items || []);
      setTotals({
        totalOpenUSD: Number(result?.totalOpenUSD || 0),
        totalOverdueUSD: Number(result?.totalOverdueUSD || 0),
        openCount: Number(result?.openCount || 0),
        todayRate: Number(result?.todayRate || 0),
      });
    } catch (err) {
      console.error('[CreditReceivablesPage] Error al cargar la cartera CxC:', err);
      setLoadError(err?.message || 'No se pudo cargar la cartera de cuentas por cobrar.');
      setItems([]);
    } finally {
      setIsLoading(false);
    }
  }, [status, overdueOnly, debouncedSearch]);

  useEffect(() => {
    loadList();
  }, [loadList]);

  const overdueAlert = hasOverdueAlert(totals.totalOverdueUSD);

  const handleCancelFilters = () => {
    setSearchQuery('');
    setStatus('Todos');
    setOverdueOnly(false);
  };

  const handlePaymentSuccess = async (result) => {
    setPaymentRow(null);
    await loadList();
    setActionError(null);
    setSuccessMessage({
      text: `Abono registrado (comprobante N° ${result?.paymentId ?? '—'}): ${formatPrimary(result?.amountUSD || 0)} `
        + (isDual
          ? `(${formatSecondary(result?.amountBsS || 0)} a la tasa ${formatNumberEs(result?.paymentRate || 0)}) `
          : '')
        + (result?.isReplay ? ' — replay idempotente sin nuevos movimientos.' : '.'),
      paymentId: result?.paymentId ?? null,
    });
  };

  const handleReturnSuccess = async (result) => {
    setReturnRow(null);
    await loadList();
    setActionError(null);
    setSuccessMessage({
      text: Number(result?.refundedUSD) > 0
        ? `Devolución registrada. La caja reembolsó ${formatPrimary(result.refundedUSD)}`
          + (isDual ? ` (${formatSecondary(result.refundedBsS || 0)})` : '')
          + `. Saldo pendiente: ${formatPrimary(result.balanceAfterUSD || 0)}.`
        : `Devolución registrada. Saldo pendiente actualizado: ${formatPrimary(result?.balanceAfterUSD || 0)}.`,
      paymentId: null,
    });
  };

  const handleConfirmCancel = async () => {
    if (!cancelRow || isCancelling) return;

    setIsCancelling(true);
    setActionError(null);
    try {
      await cancelCreditSale(cancelRow.saleId, cancelReason);
      setCancelRow(null);
      setCancelReason('');
      await loadList();
      setSuccessMessage({
        text: `Venta a crédito ${cancelRow.invoiceDisplay || `V${cancelRow.saleId}`} anulada correctamente. El stock y el cupo fueron restituidos sin movimientos de caja.`,
        paymentId: null,
      });
    } catch (err) {
      console.error('[CreditReceivablesPage] Error al anular la venta a crédito:', err);
      setActionError(err?.message || 'No se pudo anular la venta a crédito.');
      setCancelRow(null);
    } finally {
      setIsCancelling(false);
    }
  };

  const handleDownloadCreditSaleReceipt = async (row) => {
    if (isDownloading) return;
    setIsDownloading(true);
    setActionError(null);
    try {
      const blob = await getCreditSaleReceipt(row.saleId);
      saveReceiptBlob(blob, creditSaleReceiptFileName(row.invoiceNumber, row.saleId));
    } catch (err) {
      console.error('[CreditReceivablesPage] Error al descargar el comprobante de venta a crédito:', err);
      setActionError(err?.message || 'No se pudo descargar el comprobante de venta a crédito.');
    } finally {
      setIsDownloading(false);
    }
  };

  const handleDownloadPaymentReceipt = async (paymentId) => {
    if (isDownloading || !paymentId) return;
    setIsDownloading(true);
    setActionError(null);
    try {
      const blob = await getCreditPaymentReceipt(paymentId);
      saveReceiptBlob(blob, creditPaymentReceiptFileName(paymentId));
    } catch (err) {
      console.error('[CreditReceivablesPage] Error al descargar el comprobante de abono:', err);
      setActionError(err?.message || 'No se pudo descargar el comprobante de abono.');
    } finally {
      setIsDownloading(false);
    }
  };

  const invoiceLabel = (row) => (row.invoiceNumber ? `F${String(row.invoiceNumber).padStart(5, '0')}` : `V${row.saleId}`);

  const statusBadgeClass = (row) => {
    const key = String(row?.status || '').toLowerCase();
    return `credit-status-badge credit-status-badge--${key}`;
  };

  return (
    <div className="pending-orders-container credit-page">
      {/* ── 1. Encabezado ── */}
      <div className="pending-orders-header">
        <div>
          <h1 className="pending-orders-title">
            <HandCoins className="color-primary" size={26} /> Cuentas por Cobrar (CxC)
          </h1>
          <p className="pending-orders-desc">
            Cartera de ventas a crédito con deuda denominada en {primaryLabel}: abonos FIFO o por factura, anulación sin pagos, devoluciones con reembolso y comprobantes.
          </p>
        </div>

        <button className="btn btn-outline flex-align-center gap-2" onClick={loadList} disabled={isLoading}>
          <RefreshCw size={16} className={isLoading ? 'spin' : ''} /> Actualizar
        </button>
      </div>

      {/* ── 2. Métricas de la cartera filtrada ── */}
      <div className="credit-metrics">
        <div className="credit-metric-card">
          <span className="credit-metric-label">Cartera abierta</span>
          <span className="credit-metric-value">{formatPrimary(totals.totalOpenUSD)}</span>
          <span className="credit-metric-sub">{totals.openCount} {totals.openCount === 1 ? 'obligación' : 'obligaciones'} abiertas</span>
        </div>
        <div className={`credit-metric-card${overdueAlert ? ' credit-metric-card--danger' : ''}`}>
          <span className="credit-metric-label">En mora (fuera de gracia)</span>
          <span className="credit-metric-value">{formatPrimary(totals.totalOverdueUSD)}</span>
          <span className="credit-metric-sub">
            {overdueAlert ? 'Alerta de mora en la cartera filtrada' : 'Sin obligaciones vencidas'}
          </span>
        </div>
        {isDual && (
          <div className="credit-metric-card">
            <span className="credit-metric-label">Tasa del día</span>
            <span className="credit-metric-value">{formatSecondary(totals.todayRate)}</span>
            <span className="credit-metric-sub">{localLabel} por {primaryLabel} (proyección de saldos)</span>
          </div>
        )}
      </div>

      {/* ── 3. Filtros server-side ── */}
      <div className="credit-toolbar">
        <div className="credit-toolbar-search">
          <input
            id="credit-search-input"
            type="text"
            className="input-field"
            placeholder="Buscar por cliente, cédula/RIF o factura..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            aria-label="Buscar en la cartera de cuentas por cobrar"
          />
          <Search size={18} />
        </div>

        <div className="form-group mb-0">
          <select
            id="credit-status-filter"
            className="form-select"
            value={status}
            onChange={(e) => setStatus(e.target.value)}
            aria-label="Estado de la obligación"
          >
            {RECEIVABLE_STATUS_OPTIONS.map((option) => (
              <option key={option} value={option}>{option === 'Todos' ? 'Todos los estados' : option}</option>
            ))}
          </select>
        </div>

        <label className="credit-toolbar-check" htmlFor="credit-overdue-filter">
          <input
            id="credit-overdue-filter"
            type="checkbox"
            checked={overdueOnly}
            onChange={(e) => setOverdueOnly(e.target.checked)}
          />
          Solo en mora
        </label>

        <button type="button" className="btn btn-outline" onClick={handleCancelFilters}>
          Limpiar filtros
        </button>
      </div>

      {successMessage && (
        <div className="credit-alert-inline" role="status">
          <span><CheckCircle2 size={16} /> {successMessage.text}</span>
          {successMessage.paymentId && (
            <button
              type="button"
              className="btn btn-outline btn-sm"
              onClick={() => handleDownloadPaymentReceipt(successMessage.paymentId)}
              disabled={isDownloading}
            >
              <Download size={14} /> Descargar comprobante de abono
            </button>
          )}
        </div>
      )}

      {actionError && (
        <div className="credit-alert-inline credit-alert-inline--error" role="alert">
          <span><AlertTriangle size={16} /> {actionError}</span>
        </div>
      )}

      {loadError && (
        <div className="credit-alert-inline credit-alert-inline--error" role="alert">
          <span><AlertTriangle size={16} /> {loadError}</span>
          <button type="button" className="btn btn-outline btn-sm" onClick={loadList}>Reintentar</button>
        </div>
      )}

      {/* ── 4. Cartera ── */}
      {isLoading && items.length === 0 ? (
        <div className="credit-state" role="status">
          <Loader2 size={36} className="animate-spin color-primary mb-2" />
          <p>Cargando cartera de cuentas por cobrar...</p>
        </div>
      ) : items.length === 0 ? (
        <div className="credit-state">
          <p>No hay cuentas por cobrar que coincidan con los filtros.</p>
        </div>
      ) : (
        <>
          <div className="credit-table-wrap">
            <table className="credit-table">
              <thead>
                <tr>
                  <th>Cliente</th>
                  <th>Factura</th>
                  <th>Emisión</th>
                  <th>Vencimiento</th>
                  <th className="text-right">Saldo {primaryLabel}</th>
                  {isDual && <th className="text-right">Saldo hoy {localLabel}</th>}
                  <th>Estado</th>
                  <th className="text-right">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {items.map((row) => (
                  <tr key={row.receivableId}>
                    <td>
                      <span className="credit-customer-name">{row.customerName}</span>
                      <span className="credit-customer-sub">{row.customerCedulaOrRif}</span>
                    </td>
                    <td>{invoiceLabel(row)}</td>
                    <td>{formatDateVe(row.issuedAtUtc)}</td>
                    <td>
                      {formatDateVe(row.dueDateUtc)}
                      {row.isOverdue && <span className="credit-overdue-badge">En mora</span>}
                    </td>
                    <td className="text-right credit-amount">{formatPrimary(row.balanceUSD)}</td>
                    {isDual && (
                      <td className="text-right credit-amount">{formatSecondary(row.balanceTodayBsS || 0)}</td>
                    )}
                    <td><span className={statusBadgeClass(row)}>{row.status}</span></td>
                    <td>
                      <div className="credit-row-actions">
                        <button
                          type="button"
                          className="btn btn-primary"
                          onClick={() => setPaymentRow(row)}
                          disabled={!isReceivableOpen(row)}
                          title={isReceivableOpen(row) ? `Registrar abono a ${invoiceLabel(row)}` : 'La obligación ya está liquidada o anulada'}
                        >
                          <Wallet size={14} /> Abonar
                        </button>
                        <button
                          type="button"
                          className="btn btn-outline"
                          onClick={() => setStatementRow(row)}
                          title="Ver estado de cuenta del cliente"
                        >
                          Estado de cuenta
                        </button>
                        <button
                          type="button"
                          className="btn btn-outline"
                          onClick={() => handleDownloadCreditSaleReceipt(row)}
                          disabled={isDownloading}
                          title="Descargar comprobante de venta a crédito"
                        >
                          <Download size={14} />
                        </button>
                        <button
                          type="button"
                          className="btn btn-danger-text"
                          onClick={() => { setCancelReason(''); setCancelRow(row); }}
                          disabled={!isReceivableCancelable(row)}
                          title={isReceivableCancelable(row)
                            ? 'Anular la venta sin abonos (restituye stock y cupo)'
                            : 'La anulación simple solo procede cuando la obligación no tiene abonos'}
                        >
                          <Trash2 size={14} /> Anular
                        </button>
                        {isElevated && (
                          <button
                            type="button"
                            className="btn btn-outline"
                            onClick={() => setReturnRow(row)}
                            disabled={!canReturnReceivable(row)}
                            title="Registrar devolución total o parcial"
                          >
                            <Undo2 size={14} /> Devolver
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {/* Vista móvil (misma data, tarjetas) */}
          <div className="credit-mobile-list">
            {items.map((row) => (
              <div key={`m-${row.receivableId}`} className="credit-mobile-card">
                <div className="credit-mobile-card-head">
                  <div>
                    <span className="credit-customer-name">{row.customerName}</span>
                    <span className="credit-customer-sub">{row.customerCedulaOrRif} · {invoiceLabel(row)}</span>
                  </div>
                  <span className={statusBadgeClass(row)}>{row.status}</span>
                </div>
                <div className="credit-mobile-card-grid">
                  <div className="d-flex justify-between">
                    <span className="text-muted">Saldo {primaryLabel}</span>
                    <strong>{formatPrimary(row.balanceUSD)}</strong>
                  </div>
                  {isDual && (
                    <div className="d-flex justify-between">
                      <span className="text-muted">Saldo hoy {localLabel}</span>
                      <strong>{formatSecondary(row.balanceTodayBsS || 0)}</strong>
                    </div>
                  )}
                  <div className="d-flex justify-between">
                    <span className="text-muted">Vencimiento</span>
                    <span>{formatDateVe(row.dueDateUtc)}{row.isOverdue ? ' ⚠️' : ''}</span>
                  </div>
                </div>
                <div className="credit-row-actions mt-2">
                  <button
                    type="button"
                    className="btn btn-primary"
                    onClick={() => setPaymentRow(row)}
                    disabled={!isReceivableOpen(row)}
                  >
                    <Wallet size={14} /> Abonar
                  </button>
                  <button type="button" className="btn btn-outline" onClick={() => setStatementRow(row)}>
                    Estado de cuenta
                  </button>
                  <button
                    type="button"
                    className="btn btn-outline"
                    onClick={() => handleDownloadCreditSaleReceipt(row)}
                    disabled={isDownloading}
                    title="Descargar comprobante de venta a crédito"
                  >
                    <Download size={14} />
                  </button>
                </div>
              </div>
            ))}
          </div>
        </>
      )}

      {/* ── 5. Modales ── */}
      {paymentRow && (
        <CreditPaymentModal
          isOpen={Boolean(paymentRow)}
          onClose={() => setPaymentRow(null)}
          customerId={paymentRow.customerId}
          customerName={paymentRow.customerName}
          todayRate={totals.todayRate}
          onSuccess={handlePaymentSuccess}
        />
      )}

      {statementRow && (
        <CreditStatementModal
          isOpen={Boolean(statementRow)}
          onClose={() => setStatementRow(null)}
          customerId={statementRow.customerId}
          customerName={statementRow.customerName}
        />
      )}

      {returnRow && (
        <CreditReturnModal
          isOpen={Boolean(returnRow)}
          onClose={() => setReturnRow(null)}
          row={returnRow}
          onSuccess={handleReturnSuccess}
        />
      )}

      {cancelRow && (
        <Modal
          isOpen={Boolean(cancelRow)}
          onClose={() => setCancelRow(null)}
          title="Anular Venta a Crédito"
          maxWidth="480px"
        >
          <div className="text-center py-2">
            <div className="credit-cancel-icon">
              <AlertTriangle size={24} />
            </div>
            <h3 className="font-bold text-base mb-2 text-primary">
              ¿Anular la venta a crédito {invoiceLabel(cancelRow)}?
            </h3>
            <p className="text-xs text-muted mb-3 credit-modal-message">
              La anulación simple solo procede sin abonos registrados: restituye el stock y el cupo del cliente, sin movimientos de caja. Esta acción no se puede deshacer.
            </p>

            <div className="form-group text-left">
              <label htmlFor="credit-cancel-reason">Motivo (opcional)</label>
              <textarea
                id="credit-cancel-reason"
                className="form-control"
                maxLength={500}
                rows={2}
                value={cancelReason}
                onChange={(e) => setCancelReason(e.target.value)}
                disabled={isCancelling}
              />
            </div>

            <div className="credit-modal-actions">
              <button type="button" className="btn btn-outline" onClick={() => setCancelRow(null)} disabled={isCancelling}>
                Cancelar
              </button>
              <button type="button" className="btn btn-danger" onClick={handleConfirmCancel} disabled={isCancelling}>
                {isCancelling ? <Loader2 size={16} className="animate-spin" /> : <Trash2 size={16} />} Sí, Anular Venta
              </button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}
