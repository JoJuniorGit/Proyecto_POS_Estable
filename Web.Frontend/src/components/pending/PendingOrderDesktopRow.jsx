import { ChevronDown, ChevronRight, CheckCircle, Edit2, LockOpen, ShieldCheck } from 'lucide-react';
import { formatNumberEs, formatBsS, formatUSD, formatQuantity } from '../../utils/formatters';
import PendingOrderLockBadge from './PendingOrderLockBadge';

export default function PendingOrderDesktopRow({ sale, isExpanded, lockInfo, isElevated, onToggle, onCheckout, onEdit, onForceRelease }) {
  return (
    <>
      <tr
        className={`cursor-pointer${isExpanded ? ' ppo-row-expanded' : ''}`}
        onClick={() => onToggle(sale.id)}
      >
        <td>
          <div className="d-flex flex-align-center gap-2">
            {isExpanded ? <ChevronDown size={18} /> : <ChevronRight size={18} />}
            <div>
              <strong>Pedido #{sale.id}</strong>
              <div className="ppo-subtext">
                {new Date(sale.date).toLocaleDateString()} {new Date(sale.date).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
              </div>
              <PendingOrderLockBadge lockInfo={lockInfo} />
            </div>
          </div>
        </td>

        <td className="ppo-maxw220">
          <div
            className="font-medium text-truncate ppo-maxw220"
            title={sale.customerName || sale.customer?.name || 'Consumidor Final'}
          >
            <strong>{sale.customerName || sale.customer?.name || 'Consumidor Final'}</strong>
          </div>
          <div className="ppo-subtext">{sale.customerCedula || sale.customer?.cedulaOrRif || 'V-00000000'}</div>
        </td>

        <td className="text-right text-nowrap">
          <div className="amount-bss font-bold total-bss-highlight ppo-total-bss">
            {formatBsS(sale.totalBsS)}
          </div>
          <div className="amount-usd ppo-usd-sub">
            {formatUSD(sale.totalUSD)}
          </div>
        </td>

        <td className="text-right" onClick={(e) => e.stopPropagation()}>
          <div className="d-flex gap-2 justify-end">
            <button
              className="btn btn-sm btn-primary gap-1"
              onClick={() => onCheckout(sale)}
              disabled={lockInfo.isLockedByOther}
              title={lockInfo.isLockedByOther ? lockInfo.label : undefined}
            >
              <CheckCircle size={14} /> Cobrar
            </button>

            <button
              className="btn btn-sm btn-outline flex-align-center gap-1"
              onClick={() => onEdit(sale)}
              disabled={lockInfo.isLockedByOther}
              title={lockInfo.isLockedByOther ? lockInfo.label : undefined}
            >
              <Edit2 size={14} /> Editar
            </button>

            {isElevated && lockInfo.isLockedByOther && (
              <button
                type="button"
                className="btn btn-sm btn-outline flex-align-center gap-1"
                onClick={() => onForceRelease(sale)}
                title={`Liberar el bloqueo del pedido #${sale.id}`}
              >
                <LockOpen size={14} /> Liberar
              </button>
            )}
          </div>
        </td>
      </tr>

      {/* Expanded Detail Desktop */}
      {isExpanded && (
        <tr className="history-detail-row">
          <td colSpan="4" className="history-detail-cell ppo-detail-border">
            <div className="grid grid-2 ppo-detail-grid">
              
              {/* Products Section */}
              <div>
                <h4 className="ppo-detail-h4">📦 Productos del Pedido</h4>
                <table className="w-full ppo-detail-table">
                  <thead>
                    <tr className="border-bottom ppo-detail-throw">
                      <th>Producto</th>
                      <th className="text-right">Cant.</th>
                      <th className="text-right">P. Unidad</th>
                      <th className="text-right">Subtotal</th>
                    </tr>
                  </thead>
                  <tbody>
                    {sale.items.map(item => (
                      <tr key={item.id} className="border-bottom-dashed">
                        <td className="ppo-cell-pad">{item.displayProductName || (item.unitOfMeasure && item.unitOfMeasure !== 'Und' ? `${item.productName} (${item.unitOfMeasure})` : item.productName)}</td>
                        <td className="text-right text-nowrap">{formatQuantity(item.quantity)}</td>
                        <td className="amount-bss text-right text-nowrap">{formatBsS(item.unitPriceBsS)}</td>
                        <td className="amount-bss text-right text-nowrap font-semibold">{formatBsS(item.subtotalBsS)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              {/* Payments / Abonos Section */}
              <div>
                <h4 className="ppo-detail-h4 flex-align-center ppo-abonos-h4">
                  <ShieldCheck size={18} className="text-primary" /> Historial de Abonos
                </h4>
                {sale.payments.length === 0 ? (
                  <p className="ppo-no-abonos">No hay abonos registrados para esta cuenta aún.</p>
                ) : (
                  <table className="w-full ppo-detail-table">
                    <thead>
                      <tr className="border-bottom ppo-detail-throw">
                        <th>Fecha</th>
                        <th>Método</th>
                        <th className="text-right">Monto Bs.S</th>
                        <th className="text-right">Tasa Usada</th>
                        <th className="text-right">Abono USD</th>
                      </tr>
                    </thead>
                    <tbody>
                      {sale.payments.map(p => (
                        <tr key={p.id} className="border-bottom-dashed">
                          <td className="ppo-cell-pad">{new Date(p.createdAt || sale.date).toLocaleDateString()}</td>
                          <td>{p.paymentMethodName}</td>
                          <td className="amount-bss text-right text-nowrap">{formatBsS(p.amountBsS)}</td>
                          <td className="text-right text-nowrap">{formatNumberEs(p.exchangeRate)} Bs/$</td>
                          <td className="amount-usd text-right text-nowrap font-bold">+{formatUSD(p.amount)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </div>

            </div>
          </td>
        </tr>
      )}
    </>
  );
}
