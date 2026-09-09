import React, { useState, useEffect, useCallback, useRef } from 'react';
import { getPendingSalesPage, completeSale, addPaymentToHoldSale, cancelSale } from '../services/salesApi';
import { useExchangeRate } from '../context/ExchangeRateContext';
import CheckoutModal from '../components/checkout/CheckoutModal';
import EditSaleModal from '../components/pos/EditSaleModal';
import SuccessScreen from '../components/checkout/SuccessScreen';
import Modal from '../components/ui/Modal';
import { formatNumberEs, formatBsS, formatUSD, formatQuantity } from '../utils/formatters';
import { Search, Loader2, Clock, ChevronRight, ChevronDown, RefreshCw, CheckCircle, ShieldCheck, Edit2, User, Trash2, AlertTriangle } from 'lucide-react';
import './PendingOrdersPage.css';

export default function PendingOrdersPage() {
  const [sales, setSales] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [expandedSaleId, setExpandedSaleId] = useState(null);
  const [selectedSaleId, setSelectedSaleId] = useState(null);
  const [isDeleting, setIsDeleting] = useState(false);
  const [showConfirmCancel, setShowConfirmCancel] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const [completedLiquidation, setCompletedLiquidation] = useState(null);
  
  const { exchangeRate } = useExchangeRate();

  // 8.27-A05: claves de idempotencia estables por lote de abonos (saleId -> batchId).
  // Se reutilizan en reintentos para que un fallo de red no duplique pagos ya acreditados.
  const abonoBatchKeysRef = useRef(new Map());

  // Modals state
  const [selectedSaleForCheckout, setSelectedSaleForCheckout] = useState(null);
  const [selectedSaleForEdit, setSelectedSaleForEdit] = useState(null);

  // 8.14-N1: paginación de UI — página actual (offset) y si hay más para el botón "Ver más".
  const PAGE_SIZE = 200;
  const [hasMore, setHasMore] = useState(false);
  const [pageOffset, setPageOffset] = useState(0);

  const loadPendingData = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const { items, totalCount } = await getPendingSalesPage({ limit: PAGE_SIZE, offset: 0 });
      setSales(items || []);
      setPageOffset(items?.length || 0);
      setHasMore(totalCount > (items?.length || 0));
    } catch (err) {
      console.error(err);
      setError('No se pudieron cargar las cuentas abiertas.');
    } finally {
      setLoading(false);
    }
  }, []);

  // 8.14-N1: carga la siguiente página y la agrega a la lista ("Ver más").
  const loadMore = useCallback(async () => {
    if (loading) return;
    setLoading(true);
    setError(null);
    try {
      const { items, totalCount } = await getPendingSalesPage({ limit: PAGE_SIZE, offset: pageOffset });
      if (items.length > 0) {
        setSales(prev => [...(prev || []), ...items]);
        setPageOffset(prev => prev + items.length);
      }
      setHasMore(totalCount > pageOffset + items.length);
    } catch (err) {
      console.error(err);
      setError('No se pudieron cargar más cuentas.');
    } finally {
      setLoading(false);
    }
  }, [loading, pageOffset]);

  useEffect(() => {
    loadPendingData();

    const handleRefreshSignal = () => loadPendingData();
    window.addEventListener('onHoldSalesUpdated', handleRefreshSignal);

    return () => {
      window.removeEventListener('onHoldSalesUpdated', handleRefreshSignal);
    };
    // 8.6-M3: exchangeRate NO depende del re-fetch — la lista se refresca con el evento
    // onHoldSalesUpdated (que el servidor emite al recalcular). Evita recargar por cada tick de tasa.
  }, [loadPendingData]);

  const toggleExpand = (id) => {
    setSelectedSaleId(id);
    setExpandedSaleId(prev => prev === id ? null : id);
  };

  const handleEditSale = (sale) => {
    setSelectedSaleId(sale.id);
    setSelectedSaleForEdit(sale);
  };

  const selectedSale = sales.find(s => s.id === selectedSaleId) || sales.find(s => s.id === expandedSaleId);
  const selectedSaleTotalPaidUSD = selectedSale?.totalPaidUSD || (selectedSale?.payments?.reduce((acc, p) => acc + (p.amount || 0), 0)) || 0;
  const hasPayments = selectedSaleTotalPaidUSD > 0 || (selectedSale?.payments && selectedSale.payments.length > 0);
  const canCancelSelectedSale = Boolean(selectedSale && !hasPayments);

  const handleConfirmCancelSale = async () => {
    if (!selectedSale || !canCancelSelectedSale) return;
    setIsDeleting(true);
    setError(null);
    try {
      await cancelSale(selectedSale.id);
      setShowConfirmCancel(false);
      setSelectedSaleId(null);
      if (expandedSaleId === selectedSale.id) setExpandedSaleId(null);
      await loadPendingData();
      window.dispatchEvent(new CustomEvent('onHoldSalesUpdated'));
    } catch (err) {
      console.error('[PendingOrdersPage] Error al anular pedido:', err);
      const msg = err.response?.data?.message || err.response?.data || err.message || 'Error al anular el pedido.';
      setError(typeof msg === 'string' ? msg : 'Error al anular el pedido.');
      setShowConfirmCancel(false);
    } finally {
      setIsDeleting(false);
    }
  };

  const filteredSales = sales.filter(s => {
    if (!searchQuery) return true;
    const q = searchQuery.toLowerCase();
    const customerName = s.customer?.name?.toLowerCase() || '';
    const customerRif = s.customer?.cedulaOrRif?.toLowerCase() || '';
    const saleIdStr = s.id.toString();
    return customerName.includes(q) || customerRif.includes(q) || saleIdStr.includes(q);
  });

  return (
    <div className="pending-orders-container">
      {/* ── 1. Encabezado Adaptativo ── */}
      <div className="pending-orders-header">
        <div>
          <h1 className="pending-orders-title">
            <Clock className="color-primary" size={26} /> Cuentas Abiertas (Pedidos en Espera)
          </h1>
          <p className="pending-orders-desc">
            Gestiona abonos en Bolívares protegidos contra devaluación y liquida cuentas sin descontar inventario previamente.
          </p>
        </div>

        <div className="d-flex gap-2 flex-wrap flex-align-center">
          {selectedSale ? (
            <button
              className="btn btn-danger flex-align-center gap-2 font-bold ppo-danger-btn ppo-danger-btn-active"
              onClick={() => setShowConfirmCancel(true)}
              disabled={!canCancelSelectedSale || isDeleting}
              title={hasPayments ? "No se puede anular un pedido con abonos acumulados" : `Anular pedido #${selectedSale.id}`}
            >
              <Trash2 size={16} /> Anular Pedido #{selectedSale.id}
            </button>
          ) : (
            <button
              className="btn btn-danger flex-align-center gap-2 font-bold ppo-danger-btn ppo-danger-btn-disabled"
              disabled={true}
              title="Seleccione un pedido en la lista para anularlo"
            >
              <Trash2 size={16} /> Anular Pedido
            </button>
          )}

          <button className="btn btn-outline flex-align-center gap-2" onClick={loadPendingData} disabled={loading}>
            <RefreshCw size={16} className={loading ? 'spin' : ''} /> Actualizar
          </button>
        </div>
      </div>

      {/* ── 2. Controles de Búsqueda y Tasa BCV ── */}
      <div className="pending-orders-controls-bar">
        <div className="pending-orders-search-wrapper">
          <input
            type="text"
            className="input-field w-full ppo-search-input"
            placeholder="Buscar por Cliente, Cédula/RIF o ID..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
          />
          <Search size={18} className="ppo-search-icon opacity-50" />
        </div>

        <div className="pending-orders-bcv-badge">
          Tasa BCV del Día: <span className="rate-value">{formatNumberEs(exchangeRate)} Bs/$</span>
        </div>
      </div>

      {error && (
        <div className="alert-box danger-alert mb-4">
          {error}
        </div>
      )}

      {loading && sales.length === 0 ? (
        <div className="ppo-loading text-center">
          <Loader2 size={40} className="spin color-primary mb-4 mx-auto" />
          <p>Cargando cuentas abiertas...</p>
        </div>
      ) : filteredSales.length === 0 ? (
        <div className="card p-5 text-center text-muted">
          <p className="ppo-faded">No hay pedidos en espera pendientes.</p>
        </div>
      ) : (
        <>
          {/* ── 3A. VISTA ESCRITORIO (TABLA TRADICIONAL) ── */}
          <div className="pending-desktop-view">
            <table>
              <thead>
                <tr>
                  <th>ID / Fecha</th>
                  <th>Cliente</th>
                  <th className="text-right">Total Factura</th>
                  <th className="text-right">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {filteredSales.map((sale) => {
                  const isExpanded = expandedSaleId === sale.id;

                  return (
                    <React.Fragment key={sale.id}>
                      <tr
                        className="cursor-pointer"
                        style={{
                          backgroundColor: isExpanded ? 'rgba(99, 102, 241, 0.06)' : 'transparent',
                        }}
                        onClick={() => toggleExpand(sale.id)}
                      >
                        <td>
                          <div className="d-flex flex-align-center gap-2">
                            {isExpanded ? <ChevronDown size={18} /> : <ChevronRight size={18} />}
                            <div>
                              <strong>Pedido #{sale.id}</strong>
                              <div className="ppo-subtext">
                                {new Date(sale.date).toLocaleDateString()} {new Date(sale.date).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                              </div>
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
                              onClick={() => setSelectedSaleForCheckout(sale)}
                            >
                              <CheckCircle size={14} /> Cobrar
                            </button>

                            <button
                              className="btn btn-sm btn-outline flex-align-center gap-1"
                              onClick={() => handleEditSale(sale)}
                            >
                              <Edit2 size={14} /> Editar
                            </button>
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
                    </React.Fragment>
                  );
                })}
              </tbody>
            </table>
          </div>

          {/* ── 3B. VISTA MÓVIL (DISEÑO DE TARJETAS / CARD LAYOUT - OPCIÓN B) ── */}
          <div className="pending-mobile-view">
            {filteredSales.map((sale) => {
              const isExpanded = expandedSaleId === sale.id;
              const totalPaidUsd = sale.totalPaidUSD || 0;
              const remainingUsd = sale.remainingBalanceUSD || 0;
              const totalPaidBsS = (sale.payments || []).reduce((acc, p) => acc + (p.amountBsS > 0 ? p.amountBsS : (p.amount || 0) * (p.exchangeRate || exchangeRate)), 0);
              const remainingBsS = Math.max(0, (sale.totalBsS !== undefined && sale.totalBsS > 0 ? sale.totalBsS : remainingUsd * exchangeRate) - totalPaidBsS);

              return (
                <div key={sale.id} className="pending-mobile-card">
                  {/* Card Header */}
                  <div className="pending-mobile-card-header">
                    <div>
                      <div className="font-bold text-base flex-align-center gap-1 cursor-pointer" onClick={() => toggleExpand(sale.id)}>
                        {isExpanded ? <ChevronDown size={18} /> : <ChevronRight size={18} />}
                        Pedido #{sale.id}
                      </div>
                      <div className="text-xs text-muted mt-1">
                        {new Date(sale.date).toLocaleDateString()} {new Date(sale.date).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                      </div>
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
                      onClick={() => setSelectedSaleForCheckout(sale)}
                    >
                      <CheckCircle size={18} /> Cobrar
                    </button>

                    <div className="d-flex gap-2">
                      <button
                        className="btn btn-outline flex-1 flex-align-center justify-center gap-1 ppo-btn-sm38"
                        onClick={() => handleEditSale(sale)}
                      >
                        <Edit2 size={16} /> Editar Pedido
                      </button>
                      <button
                        className="btn btn-outline flex-1 flex-align-center justify-center gap-1 ppo-btn-sm38"
                        onClick={() => toggleExpand(sale.id)}
                      >
                        {isExpanded ? <ChevronDown size={16} /> : <ChevronRight size={16} />} Detalle ({sale.items.length})
                      </button>
                    </div>
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
            })}
          </div>

          {/* 8.14-N1: botón "Ver más" para paginar la cola sin perder las ya cargadas. */}
          {hasMore && (
            <div className="text-center mt-3 mb-1">
              <button
                type="button"
                className="btn btn-outline flex-align-center gap-2 mx-auto ppo-load-more"
                onClick={loadMore}
                disabled={loading}
              >
                {loading ? <Loader2 size={16} className="animate-spin" /> : <ChevronDown size={16} />}
                {loading ? 'Cargando...' : 'Ver más cuentas'}
              </button>
            </div>
          )}
        </>
      )}

      {/* Checkout Modal for Completing OnHold Sales */}
      {selectedSaleForCheckout && (
        <CheckoutModal
          isOpen={!!selectedSaleForCheckout}
          onClose={() => setSelectedSaleForCheckout(null)}
          overrideSale={selectedSaleForCheckout}
          onCompleteSale={async (paymentList, roundingAdjustment, isPendingPickup, idempotencyKey) => {
            try {
              const targetSaleId = selectedSaleForCheckout.id;
              const currentPaidUsd = selectedSaleForCheckout.totalPaidUSD || 0;
              const totalUsd = selectedSaleForCheckout.totalUSD || 0;
              const paidNowUsd = paymentList.reduce((acc, p) => acc + (p.amount || 0), 0);
              const remainingDebtAfterUsd = totalUsd - (currentPaidUsd + paidNowUsd);
              const isFullyCompleted = remainingDebtAfterUsd <= 0.05;

              if (isFullyCompleted) {
                const invoiceNumber = await completeSale(targetSaleId, exchangeRate, paymentList, roundingAdjustment, null, isPendingPickup, idempotencyKey);
                setSelectedSaleForCheckout(null);
                await loadPendingData();
                window.dispatchEvent(new CustomEvent('pendingPickupsUpdated'));

                if (isPendingPickup) {
                  setCompletedLiquidation({
                    invoiceNumber: invoiceNumber > 0 ? invoiceNumber : targetSaleId,
                    title: "¡Pedido Pagado y Enviado a Retiros Pendientes!",
                    badgeText: `Factura N° ${invoiceNumber > 0 ? invoiceNumber.toString().padStart(6, '0') : targetSaleId}`,
                    message: "La factura fue pagada al 100% y los productos quedaron resguardados en custodia para su retiro físico posterior.",
                    buttonText: "Aceptar"
                  });
                } else {
                  setCompletedLiquidation({
                    invoiceNumber: invoiceNumber > 0 ? invoiceNumber : targetSaleId,
                    title: "¡Cuenta Liquidada con Éxito!",
                    badgeText: `Factura N° ${invoiceNumber > 0 ? invoiceNumber.toString().padStart(6, '0') : targetSaleId}`,
                    message: "La factura fue completada en su totalidad y el inventario ha sido descontado correctamente.",
                    buttonText: "Aceptar"
                  });
                }
              } else {
                let batchId = abonoBatchKeysRef.current.get(targetSaleId);
                if (!batchId) {
                  batchId = (typeof crypto !== 'undefined' && crypto.randomUUID
                    ? crypto.randomUUID()
                    : `abono-${targetSaleId}-${Date.now()}`);
                  abonoBatchKeysRef.current.set(targetSaleId, batchId);
                }
                for (let i = 0; i < paymentList.length; i++) {
                  await addPaymentToHoldSale(targetSaleId, {
                    paymentMethodId: paymentList[i].paymentMethodId,
                    amountBsS: paymentList[i].amountBsS || paymentList[i].amountLocal,
                    exchangeRate: exchangeRate,
                    referenceNumber: paymentList[i].referenceNumber || null
                  }, `${batchId}-${i}`);
                }
                abonoBatchKeysRef.current.delete(targetSaleId);
                setSelectedSaleForCheckout(null);
                await loadPendingData();

                setCompletedLiquidation({
                  invoiceNumber: null,
                  title: "¡Abono Registrado con Éxito!",
                  badgeText: `Pedido N° #${targetSaleId}`,
                  message: "El abono fue registrado exitosamente en el historial de abonos anti-devaluación de la cuenta.",
                  buttonText: "Aceptar"
                });
              }
            } catch (err) {
              const msg = err.response?.data?.message || err.response?.data?.Message || (typeof err.response?.data === 'string' ? err.response?.data : null) || err.message || 'Error al procesar la operación.';
              setError(msg);
            }
          }}
        />
      )}

      {/* Overlay de Éxito / Confirmación de Liquidación */}
      {completedLiquidation && (
        <SuccessScreen
          invoiceNumber={completedLiquidation.invoiceNumber}
          title={completedLiquidation.title}
          badgeText={completedLiquidation.badgeText}
          message={completedLiquidation.message}
          buttonText={completedLiquidation.buttonText}
          type="checkout"
          onClose={() => setCompletedLiquidation(null)}
        />
      )}

      {/* Modal Dedicado de Edición de Pedido en Espera */}
      {selectedSaleForEdit && (
        <EditSaleModal
          isOpen={!!selectedSaleForEdit}
          onClose={() => setSelectedSaleForEdit(null)}
          sale={selectedSaleForEdit}
          exchangeRate={exchangeRate}
          onSuccess={loadPendingData}
        />
      )}

      {/* Modal de Confirmación Estilizado para Anulación desde la Sección */}
      {showConfirmCancel && selectedSale && (
        <Modal isOpen={true} onClose={() => setShowConfirmCancel(false)} title="Confirmar Anulación de Pedido" maxWidth="440px">
          <div className="text-center py-2">
            <div className="ppo-cancel-icon">
              <AlertTriangle size={24} />
            </div>
            <h3 className="font-bold text-base mb-2 text-primary">¿Está seguro de que desea anular el Pedido #{selectedSale.id}?</h3>
            <p className="text-xs text-muted mb-4 ppo-confirm-note">
              Esta acción anulará el pedido sin descontar caja y liberará las reservas asociadas. Esta acción no se puede deshacer.
            </p>
            <div className="d-flex justify-center gap-3">
              <button type="button" className="btn btn-outline" onClick={() => setShowConfirmCancel(false)} disabled={isDeleting}>
                Cancelar
              </button>
              <button type="button" className="btn btn-danger d-inline-flex flex-align-center gap-1" onClick={handleConfirmCancelSale} disabled={isDeleting}>
                {isDeleting ? <Loader2 size={16} className="animate-spin" /> : <Trash2 size={16} />}
                Sí, Anular Pedido
              </button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}
