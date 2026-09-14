import { ChevronDown, ChevronRight, CheckCircle, Edit2, LockOpen, ShieldCheck, User } from 'lucide-react';
import { formatBsS, formatUSD, formatQuantity } from '../../utils/formatters';
import PendingOrderLockBadge from './PendingOrderLockBadge';

export default function PendingOrderMobileCard({ sale, isExpanded, lockInfo, isElevated, exchangeRate, onToggle, onCheckout, onEdit, onForceRelease }) {
  const totalPaidUsd = sale.totalPaidUSD || 0;
  const remainingUsd = sale.remainingBalanceUSD || 0;
  const totalPaidBsS = (sale.payments || []).reduce((acc, p) => acc + (p.amountBsS > 0 ? p.amountBsS : (p.amount || 0) * (p.exchangeRate || exchangeRate)), 0);
  const remainingBsS = Math.max(0, (sale.totalBsS !== undefined && sale.totalBsS > 0 ? sale.totalBsS : remainingUsd * exchangeRate) - totalPaidBsS);

  return (
    <div className="pending-mobile-card">
      {/* Card Header */}
      <div className="pending-mobile-card-header">
        <div>
          <div className="font-bold text-base flex-align-center gap-1 cursor-pointer" onClick={() => onToggle(sale.id)}>
            {isExpanded ? <ChevronDown size={18} /> : <ChevronRight size={18} />}
            Pedido #{sale.id}
          </div>
          <div className="text-xs text-muted mt-1">
            {new Date(sale.date).toLocaleDateString()} {new Date(sale.date).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
          </div>
          <PendingOrderLockBadge lockInfo={lockInfo} />
        </div>
      </div>

      {/* Customer Info Box */}
      <div className="pending-mobile-card-customer">
        <div className="d-flex flex-align-center gap-1 font-bold pending-mobile-card-customer-name overflow-hidden">
          <User size={15} className="text-muted flex-shrink-0" />
          <span
            title={sale.customerName || sale.customer?.name || 'Consumidor Final'}
            className="text-truncate ppo-maxw100"
          >
            {sale.customerName || sale.customer?.name || 'Consumidor Final'}
          </span>
        </div>
        <div className="text-xs text-muted mt-1 ml-4">
          RIF/Cédula: {sale.customerCedula || sale.customer?.cedulaOrRif || 'V-00000000'}
        </div>
      </div>

      {/* Financial Breakdown Grid */}
      <div className="pending-mobile-card-summary">
        <div>
          <div className="text-xs text-muted mb-1">Total Factura</div>
          <div className="font-bold amount-bss total-bss-highlight">{formatBsS(sale.totalBsS)}</div>
          <div className="text-xs amount-usd">{formatUSD(sale.totalUSD)}</div>
        </div>
        <div>
          <div className="text-xs text-muted mb-1">Abonado</div>
          <div className="font-bold amount-usd">+{formatUSD(totalPaidUsd)}</div>
        </div>
        <div>
          <div className="text-xs text-muted mb-1">Deuda Pendiente</div>
          <div className="font-bold text-danger">{formatUSD(remainingUsd)}</div>
          <div className="text-xs amount-bss">≈ {formatBsS(remainingBsS)}</div>
        </div>
      </div>

      {/* Full-width Touch-friendly Action Buttons */}
      <div className="pending-mobile-card-actions">
        <button
          className="btn btn-primary w-full flex-align-center justify-center gap-2 ppo-btn-tall"
          onClick={() => onCheckout(sale)}
          disabled={lockInfo.isLockedByOther}
          title={lockInfo.isLockedByOther ? lockInfo.label : undefined}
        >
          <CheckCircle size={18} /> Cobrar
        </button>

        <div className="d-flex gap-2">
          <button
            className="btn btn-outline flex-1 flex-align-center justify-center gap-1 ppo-btn-sm38"
            onClick={() => onEdit(sale)}
            disabled={lockInfo.isLockedByOther}
            title={lockInfo.isLockedByOther ? lockInfo.label : undefined}
          >
            <Edit2 size={16} /> Editar Pedido
          </button>
          <button
            className="btn btn-outline flex-1 flex-align-center justify-center gap-1 ppo-btn-sm38"
            onClick={() => onToggle(sale.id)}
          >
            {isExpanded ? <ChevronDown size={16} /> : <ChevronRight size={16} />} Detalle ({sale.items.length})
          </button>
        </div>

        {isElevated && lockInfo.isLockedByOther && (
          <button
            type="button"
            className="btn btn-outline w-full flex-align-center justify-center gap-1 ppo-btn-sm38"
            onClick={() => onForceRelease(sale)}
            title={`Liberar el bloqueo del pedido #${sale.id}`}
          >
            <LockOpen size={16} /> Liberar Pedido
          </button>
        )}
      </div>

      {/* Mobile Collapsible Detail */}
      {isExpanded && (
        <div className="border-top-dashed pt-3 mt-1 ppo-detail-mobile">
          <h4 className="ppo-detail-h4-sm">📦 Productos del Pedido</h4>
          <div className="d-flex flex-column gap-1 ppo-items-list">
            {sale.items.map(item => (
              <div key={item.id} className="d-flex flex-between align-start border-bottom ppo-item-row">
                <div className="ppo-item-main">
                  <div><strong>{item.displayProductName || item.productName}</strong></div>
                  <div className="text-xs text-muted">{formatQuantity(item.quantity)} x {formatBsS(item.unitPriceBsS)}</div>
                </div>
                <div className="font-bold amount-bss text-right text-nowrap flex-shrink-0 ppo-item-total">{formatBsS(item.subtotalBsS)}</div>
              </div>
            ))}
          </div>

          <h4 className="ppo-detail-h4-sm flex-align-center ppo-abonos-h4-mobile">
            <ShieldCheck size={16} className="color-primary" /> Historial de Abonos
          </h4>
          {sale.payments.length === 0 ? (
            <p className="text-xs text-muted">Sin abonos previos.</p>
          ) : (
            <div className="d-flex flex-column gap-1">
              {sale.payments.map(p => (
                <div key={p.id} className="d-flex flex-between align-start border-bottom ppo-item-row ppo-payment-row">
                  <div className="ppo-item-main">
                    <span>{p.paymentMethodName}</span>
                    <span className="text-muted ml-2">({formatBsS(p.amountBsS)})</span>
                  </div>
                  <div className="font-bold amount-usd text-right text-nowrap flex-shrink-0 ppo-item-total">+{formatUSD(p.amount)}</div>
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
