import { useCallback, useEffect, useState } from 'react';
import Modal from '../ui/Modal';
import { getCreditStatement } from '../../services/creditApi';
import { creditStatusLabel, formatDateVe } from './creditLogic';
import { formatNumberEs, formatUSD } from '../../utils/formatters';
import { AlertCircle, Loader2 } from 'lucide-react';

/**
 * 8.187-T5 (S3/S6): estado de cuenta dual del cliente en el POS web. La deuda permanece en USD;
 * el Bs.S es la proyección a la tasa del día que calcula el servidor (nunca revalúa el principal).
 * Muestra el perfil, la cartera completa (abiertas y cerradas) y el historial de abonos con el
 * diferencial cambiario realizado.
 */
export default function CreditStatementModal({ isOpen, onClose, customerId, customerName }) {
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
              <span>Saldo deudor (USD):</span>
              <strong>{formatUSD(profile?.currentDebtUSD || 0)}</strong>
            </div>
            <div className="credit-amount-preview-row">
              <span>Equivalente Bs.S a la tasa del día:</span>
              <strong>Bs.S {formatNumberEs(statement.currentDebtTodayBsS || 0)}</strong>
            </div>
            <div className="credit-amount-preview-row">
              <span>Tasa del día:</span>
              <strong>{formatNumberEs(statement.todayRate || 0)} Bs.S / USD</strong>
            </div>
          </div>

          {profile && (
            <div className="credit-profile-grid">
              <div className="credit-profile-stat">
                <span className="credit-profile-stat-label">Estado</span>
                <span className="credit-profile-stat-value">{creditStatusLabel(profile.creditStatus)}</span>
              </div>
              <div className="credit-profile-stat">
                <span className="credit-profile-stat-label">Cupo</span>
                <span className="credit-profile-stat-value">{formatUSD(profile.creditLimitUSD)}</span>
              </div>
              <div className="credit-profile-stat">
                <span className="credit-profile-stat-label">Disponible</span>
                <span className="credit-profile-stat-value">{formatUSD(profile.availableCreditUSD)}</span>
              </div>
              <div className="credit-profile-stat">
                <span className="credit-profile-stat-label">Utilización</span>
                <span className="credit-profile-stat-value">{formatNumberEs(profile.utilizationPercent, 2)}%</span>
              </div>
              <div className="credit-profile-stat">
                <span className="credit-profile-stat-label">Mora</span>
                <span className="credit-profile-stat-value">{formatUSD(profile.overdueUSD)} ({profile.overdueCount})</span>
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
                      <th className="text-right">Saldo USD</th>
                      <th className="text-right">Saldo hoy Bs.S</th>
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
                        <td className="text-right credit-amount">{formatUSD(item.balanceUSD)}</td>
                        <td className="text-right credit-amount">{formatNumberEs(item.balanceTodayBsS || 0)}</td>
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
                      <th className="text-right">Monto USD</th>
                      <th className="text-right">Bs.S cobrados</th>
                      <th className="text-right">Tasa</th>
                      <th className="text-right">Diferencial Bs.S</th>
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
                        <td className="text-right credit-amount">{formatUSD(payment.amountUSD)}</td>
                        <td className="text-right credit-amount">{formatNumberEs(payment.amountBsS || 0)}</td>
                        <td className="text-right credit-amount">{formatNumberEs(payment.paymentRate || 0)}</td>
                        <td className="text-right credit-amount">{formatNumberEs(payment.fxDifferenceBsS || 0)}</td>
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
