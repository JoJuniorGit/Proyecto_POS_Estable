import { useCallback, useEffect, useState } from 'react';
import Modal from '../ui/Modal';
import { getCreditStatement } from '../../services/creditApi';
import { creditStatusLabel, formatDateVe } from './creditLogic';
import { formatNumberEs } from '../../utils/formatters';
import { useMonetaryProfile } from '../../context/MonetaryProfileContext';
import { displayCurrencyCode } from '../../utils/monetaryProfile';
import { resolveLocalCurrencyProfile } from '../register/registerFormLogic';
import { AlertCircle, Loader2 } from 'lucide-react';

/**
 * 8.211-T4b2b2b (S9): etiquetas del perfil monetario activo para el estado de cuenta CxC. La
 * deuda se denomina en la primaria; las columnas locales sólo se muestran en `Dual`.
 * @param {{ mode?: string, primaryCurrencyCode?: string, secondaryCurrencyCode?: string }} profile
 */
export function resolveCreditStatementCurrencyLabels(profile) {
  const local = resolveLocalCurrencyProfile(profile);
  return {
    isDual: local.isDual,
    primaryLabel: displayCurrencyCode(profile?.primaryCurrencyCode) || 'USD',
    localLabel: local.label || 'Bs.S',
  };
}

/**
 * 8.187-T5 (S3/S6): estado de cuenta del cliente en el POS web. La deuda permanece en la moneda
 * primaria del perfil; en `Dual` la proyección local la calcula el servidor a la tasa del día
 * (nunca revalúa el principal). Muestra el perfil, la cartera completa (abiertas y cerradas) y el
 * historial de abonos con el diferencial cambiario realizado.
 */
export default function CreditStatementModal({ isOpen, onClose, customerId, customerName }) {
  const { profile: monetaryProfile, formatPrimary, formatSecondary } = useMonetaryProfile();
  const { isDual, primaryLabel, localLabel } = resolveCreditStatementCurrencyLabels(monetaryProfile);
  const [statement, setStatement] = useState(null);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState(null);

  const loadStatement = useCallback(async (id) => {
    setIsLoading(true);
    setError(null);
    try {
      const data = await getCreditStatement(id);
      setStatement(data);
    } catch (err) {
      setStatement(null);
      setError(err?.message || 'No se pudo cargar el estado de cuenta del cliente.');
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    if (!isOpen) return;
    setStatement(null);
    setError(null);
    if (customerId) loadStatement(customerId);
  }, [isOpen, customerId, loadStatement]);

  const profile = statement?.profile;

  return (
    <Modal isOpen={isOpen} onClose={onClose} title={`Estado de Cuenta — ${customerName || 'Cliente'}`} maxWidth="760px">
      {isLoading ? (
        <p className="credit-state" role="status">
          <Loader2 size={18} className="animate-spin" /> Cargando estado de cuenta...
        </p>
      ) : error ? (
        <div className="alert alert-danger credit-modal-message" role="alert">
          <AlertCircle size={16} /> {error}
          <button
            type="button"
            className="btn btn-outline btn-sm"
            onClick={() => customerId && loadStatement(customerId)}
          >
            Reintentar
          </button>
        </div>
      ) : statement ? (
        <>
          <div className="credit-modal-section credit-amount-preview">
            <div className="credit-amount-preview-row">
              <span>Saldo deudor ({primaryLabel}):</span>
              <strong>{formatPrimary(profile?.currentDebtUSD || 0)}</strong>
            </div>
            {isDual && (
              <>
                <div className="credit-amount-preview-row">
                  <span>Equivalente {localLabel} a la tasa del día:</span>
                  <strong>{formatSecondary(statement.currentDebtTodayBsS || 0)}</strong>
                </div>
                <div className="credit-amount-preview-row">
                  <span>Tasa del día:</span>
                  <strong>{formatSecondary(statement.todayRate || 0)} / 1.00 {primaryLabel}</strong>
                </div>
              </>
            )}
          </div>

          {profile && (
            <div className="credit-profile-grid">
              <div className="credit-profile-stat">
                <span className="credit-profile-stat-label">Estado</span>
                <span className="credit-profile-stat-value">{creditStatusLabel(profile.creditStatus)}</span>
              </div>
              <div className="credit-profile-stat">
                <span className="credit-profile-stat-label">Cupo</span>
                <span className="credit-profile-stat-value">{formatPrimary(profile.creditLimitUSD)}</span>
              </div>
              <div className="credit-profile-stat">
                <span className="credit-profile-stat-label">Disponible</span>
                <span className="credit-profile-stat-value">{formatPrimary(profile.availableCreditUSD)}</span>
              </div>
              <div className="credit-profile-stat">
                <span className="credit-profile-stat-label">Utilización</span>
                <span className="credit-profile-stat-value">{formatNumberEs(profile.utilizationPercent, 2)}%</span>
              </div>
              <div className="credit-profile-stat">
                <span className="credit-profile-stat-label">Mora</span>
                <span className="credit-profile-stat-value">{formatPrimary(profile.overdueUSD)} ({profile.overdueCount})</span>
              </div>
            </div>
          )}

          <div className="credit-statement-section">
            <h4>Facturas del cliente</h4>
            {(statement.receivables || []).length === 0 ? (
              <p className="text-muted">Sin obligaciones registradas.</p>
            ) : (
              <div className="credit-table-wrap">
                <table className="credit-table credit-table--compact">
                  <thead>
                    <tr>
                      <th>Factura</th>
                      <th>Emisión</th>
                      <th>Vencimiento</th>
                      <th className="text-right">Saldo {primaryLabel}</th>
                      {isDual && <th className="text-right">Saldo hoy {localLabel}</th>}
                      <th>Estado</th>
                    </tr>
                  </thead>
                  <tbody>
                    {statement.receivables.map((item) => (
                      <tr key={item.receivableId}>
                        <td>{item.invoiceNumber ? `F${String(item.invoiceNumber).padStart(5, '0')}` : `V${item.saleId}`}</td>
                        <td>{formatDateVe(item.issuedAtUtc)}</td>
                        <td>
                          {formatDateVe(item.dueDateUtc)}
                          {item.isOverdue && <span className="credit-overdue-badge">Vencida</span>}
                        </td>
                        <td className="text-right credit-amount">{formatPrimary(item.balanceUSD)}</td>
                        {isDual && (
                          <td className="text-right credit-amount">{formatSecondary(item.balanceTodayBsS || 0)}</td>
                        )}
                        <td><span className="credit-status-badge">{item.status}</span></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>

          <div className="credit-statement-section">
            <h4>Historial de abonos</h4>
            {(statement.payments || []).length === 0 ? (
              <p className="text-muted">Sin abonos registrados.</p>
            ) : (
              <div className="credit-table-wrap">
                <table className="credit-table credit-table--compact">
                  <thead>
                    <tr>
                      <th>Fecha</th>
                      <th>Método</th>
                      <th className="text-right">Monto {primaryLabel}</th>
                      {isDual && (
                        <>
                          <th className="text-right">{localLabel} cobrados</th>
                          <th className="text-right">Tasa</th>
                          <th className="text-right">Diferencial {localLabel}</th>
                        </>
                      )}
                    </tr>
                  </thead>
                  <tbody>
                    {statement.payments.map((payment) => (
                      <tr key={payment.paymentId}>
                        <td>{formatDateVe(payment.receivedAtUtc)}</td>
                        <td>
                          {payment.paymentMethodName || '—'}
                          {payment.referenceNumber && <span className="credit-customer-sub">Ref: {payment.referenceNumber}</span>}
                        </td>
                        <td className="text-right credit-amount">{formatPrimary(payment.amountUSD)}</td>
                        {isDual && (
                          <>
                            <td className="text-right credit-amount">{formatSecondary(payment.amountBsS || 0)}</td>
                            <td className="text-right credit-amount">{formatNumberEs(payment.paymentRate || 0)}</td>
                            <td className="text-right credit-amount">{formatSecondary(payment.fxDifferenceBsS || 0)}</td>
                          </>
                        )}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>

          <div className="credit-modal-actions">
            <button type="button" className="btn btn-outline" onClick={onClose}>
              Cerrar
            </button>
          </div>
        </>
      ) : null}
    </Modal>
  );
}
