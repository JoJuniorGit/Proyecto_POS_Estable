import React, { useState, useEffect, useCallback } from 'react';
import { getPendingSales, completeSale, addPaymentToHoldSale, cancelSale } from '../services/salesApi';
import { getActivePaymentMethods } from '../services/paymentApi';
import { useExchangeRate } from '../context/ExchangeRateContext';
import { useCart } from '../context/CartContext';
import { useAuth } from '../context/AuthContext';
import CheckoutModal from '../components/checkout/CheckoutModal';
import EditSaleModal from '../components/pos/EditSaleModal';
import SuccessScreen from '../components/checkout/SuccessScreen';
import Modal from '../components/ui/Modal';
import { formatNumberEs, formatBsS, formatUSD, formatQuantity } from '../utils/formatters';
import { Search, Loader2, Clock, ChevronRight, ChevronDown, RefreshCw, CheckCircle, ShieldCheck, Edit2, User, Trash2, AlertTriangle } from 'lucide-react';
import './PendingOrdersPage.css';

export default function PendingOrdersPage({ onNavigate }) {
  const { user } = useAuth();
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
  const { loadExistingSale } = useCart();
  const [paymentMethods, setPaymentMethods] = useState([]);

  // Modals state
  const [selectedSaleForCheckout, setSelectedSaleForCheckout] = useState(null);
  const [selectedSaleForEdit, setSelectedSaleForEdit] = useState(null);

  const loadPendingData = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [pendingData, methodsData] = await Promise.all([
        getPendingSales(),
        getActivePaymentMethods(),
      ]);
      setSales(pendingData);
      setPaymentMethods(methodsData);
    } catch (err) {
      console.error(err);
      setError('No se pudieron cargar las cuentas abiertas.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadPendingData();

    const handleRefreshSignal = () => loadPendingData();
    window.addEventListener('onHoldSalesUpdated', handleRefreshSignal);

    return () => {
      window.removeEventListener('onHoldSalesUpdated', handleRefreshSignal);
    };
  }, [loadPendingData, exchangeRate]);

  const toggleExpand = (id) => {
    setSelectedSaleId(id);
    setExpandedSaleId(prev => prev === id ? null : id);
  };

  const handleEditSale = (sale) => {
    setSelectedSaleId(sale.id);
    setSelectedSaleForEdit(sale);
  };

  const selectedSale = sales.find(s => s.id === selectedSaleId) || sales.find(s => s.id === expandedSaleId);
  const isUserAdminOrManager = user?.role === 'Admin' || user?.role === 'Manager' || user?.role === 0 || user?.role === '0';
  const selectedSaleTotalPaidUSD = selectedSale?.totalPaidUSD || (selectedSale?.payments?.reduce((acc, p) => acc + (p.amount || 0), 0)) || 0;
  const hasPayments = selectedSaleTotalPaidUSD > 0 || (selectedSale?.payments && selectedSale.payments.length > 0);
  const canCancelSelectedSale = selectedSale && isUserAdminOrManager && !hasPayments;

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
              className="btn btn-danger flex-align-center gap-2 font-bold"
              onClick={() => setShowConfirmCancel(true)}
              disabled={!canCancelSelectedSale || isDeleting}
              style={{
                boxShadow: '0 2px 8px rgba(239, 68, 68, 0.35)',
                transition: 'all 0.2s ease',
                padding: '8px 16px',
                fontSize: '0.875rem'
              }}
              title={hasPayments ? "No se puede anular un pedido con abonos acumulados" : (!isUserAdminOrManager ? "Requiere rol de Administrador o Gerente" : `Anular pedido #${selectedSale.id}`)}
            >
              <Trash2 size={16} /> Anular Pedido #{selectedSale.id}
            </button>
          ) : (
            <button
              className="btn btn-danger flex-align-center gap-2 font-bold"
              disabled={true}
              style={{
                opacity: 0.4,
                cursor: 'not-allowed',
                padding: '8px 16px',
                fontSize: '0.875rem'
              }}
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
            className="input-field"
            placeholder="Buscar por Cliente, Cédula/RIF o ID..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            style={{ width: '100%', paddingLeft: '36px' }}
          />
          <Search size={18} style={{ position: 'absolute', left: '10px', top: '50%', transform: 'translateY(-50%)', opacity: 0.5 }} />
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
        <div style={{ padding: '60px', textAlign: 'center' }}>
          <Loader2 size={40} className="spin color-primary" style={{ margin: '0 auto 16px auto' }} />
          <p>Cargando cuentas abiertas...</p>
        </div>
      ) : filteredSales.length === 0 ? (
        <div className="card p-5 text-center text-muted">
          <p style={{ opacity: 0.7 }}>No hay pedidos en espera pendientes.</p>
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
                  <th style={{ textAlign: 'right' }}>Total Factura</th>
                  <th>Estado</th>
                  <th style={{ textAlign: 'right' }}>Acciones</th>
                </tr>
              </thead>
              <tbody>
                {filteredSales.map((sale) => {
                  const isExpanded = expandedSaleId === sale.id;
                  const remainingUsd = sale.remainingBalanceUSD || 0;

                  return (
                    <React.Fragment key={sale.id}>
                      <tr
                        style={{
                          cursor: 'pointer',
                          backgroundColor: isExpanded ? 'rgba(99, 102, 241, 0.06)' : 'transparent',
                        }}
                        onClick={() => toggleExpand(sale.id)}
                      >
                        <td>
                          <div className="d-flex flex-align-center gap-2">
                            {isExpanded ? <ChevronDown size={18} /> : <ChevronRight size={18} />}
                            <div>
                              <strong>Pedido #{sale.id}</strong>
                              <div style={{ fontSize: '0.8em', opacity: 0.6 }}>
                                {new Date(sale.date).toLocaleDateString()} {new Date(sale.date).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                              </div>
                            </div>
                          </div>
                        </td>

                        <td style={{ maxWidth: '220px' }}>
                          <div
                            className="font-medium text-truncate"
                            title={sale.customerName || sale.customer?.name || 'Consumidor Final'}
                            style={{ maxWidth: '220px' }}
                          >
                            <strong>{sale.customerName || sale.customer?.name || 'Consumidor Final'}</strong>
                          </div>
                          <div style={{ fontSize: '0.8em', opacity: 0.6 }}>{sale.customerCedula || sale.customer?.cedulaOrRif || 'V-00000000'}</div>
                        </td>

                        <td className="text-right text-nowrap">
                          <div className="amount-bss font-bold" style={{ fontSize: '0.95rem' }}>
                            {formatBsS(sale.totalBsS)}
                          </div>
                          <div className="amount-usd" style={{ fontSize: '0.8em' }}>
                            {formatUSD(sale.totalUSD)}
                          </div>
                        </td>

                        <td>
                          <span className="badge badge-warning" style={{ backgroundColor: 'rgba(245, 158, 11, 0.2)', color: '#f59e0b', padding: '4px 10px', borderRadius: '12px', fontSize: '0.85em' }}>
                            En Espera
                          </span>
                        </td>

                        <td className="text-right" onClick={(e) => e.stopPropagation()}>
                          <div className="d-flex gap-2 justify-end">
                            <button
                              className="btn btn-sm btn-primary"
                              onClick={() => setSelectedSaleForCheckout(sale)}
                              style={{ gap: '4px' }}
                            >
                              <CheckCircle size={14} /> {remainingUsd <= 0.05 ? 'Cerrar / Entregar' : 'Liquidar / Abonar'}
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
                          <td colSpan="5" className="history-detail-cell" style={{ borderTop: '1px dashed rgba(99, 102, 241, 0.25)' }}>
                            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '20px' }}>
                              
                              {/* Products Section */}
                              <div>
                                <h4 style={{ margin: '0 0 10px 0' }}>📦 Productos del Pedido</h4>
                                <table style={{ width: '100%', fontSize: '0.9em', borderCollapse: 'collapse' }}>
                                  <thead>
                                    <tr style={{ borderBottom: '1px solid var(--border)', opacity: 0.7 }}>
                                      <th>Producto</th>
                                      <th className="text-right">Cant.</th>
                                      <th className="text-right">P. Unidad</th>
                                      <th className="text-right">Subtotal</th>
                                    </tr>
                                  </thead>
                                  <tbody>
                                    {sale.items.map(item => (
                                      <tr key={item.id} style={{ borderBottom: '1px dashed var(--border)' }}>
                                        <td style={{ padding: '6px 0' }}>{item.displayProductName || (item.unitOfMeasure && item.unitOfMeasure !== 'Und' ? `${item.productName} (${item.unitOfMeasure})` : item.productName)}</td>
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
                                <h4 style={{ margin: '0 0 10px 0', display: 'flex', alignItems: 'center', gap: '6px' }}>
                                  <ShieldCheck size={18} className="text-primary" /> Historial de Abonos
                                </h4>
                                {sale.payments.length === 0 ? (
                                  <p style={{ fontSize: '0.9em', opacity: 0.6 }}>No hay abonos registrados para esta cuenta aún.</p>
                                ) : (
                                  <table style={{ width: '100%', fontSize: '0.9em', borderCollapse: 'collapse' }}>
                                    <thead>
                                      <tr style={{ borderBottom: '1px solid var(--border)', opacity: 0.7 }}>
                                        <th>Fecha</th>
                                        <th>Método</th>
                                        <th className="text-right">Monto Bs.S</th>
                                        <th className="text-right">Tasa Usada</th>
                                        <th className="text-right">Abono USD</th>
                                      </tr>
                                    </thead>
                                    <tbody>
                                      {sale.payments.map(p => (
                                        <tr key={p.id} style={{ borderBottom: '1px dashed var(--border)' }}>
                                          <td style={{ padding: '6px 0' }}>{new Date(p.createdAt || sale.date).toLocaleDateString()}</td>
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
                      <div className="font-bold text-base flex-align-center gap-1" onClick={() => toggleExpand(sale.id)} style={{ cursor: 'pointer' }}>
                        {isExpanded ? <ChevronDown size={18} /> : <ChevronRight size={18} />}
                        Pedido #{sale.id}
                      </div>
                      <div className="text-xs text-muted mt-1">
                        {new Date(sale.date).toLocaleDateString()} {new Date(sale.date).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                      </div>
                    </div>

                    <span className="badge badge-warning" style={{ backgroundColor: 'rgba(245, 158, 11, 0.2)', color: '#f59e0b', padding: '4px 10px', borderRadius: '12px', fontSize: '0.8em' }}>
                      En Espera
                    </span>
                  </div>

                  {/* Customer Info Box */}
                  <div className="pending-mobile-card-customer">
                    <div className="d-flex flex-align-center gap-1 font-bold pending-mobile-card-customer-name" style={{ overflow: 'hidden', minWidth: 0 }}>
                      <User size={15} className="text-muted flex-shrink-0" />
                      <span
                        title={sale.customerName || sale.customer?.name || 'Consumidor Final'}
                        className="text-truncate"
                        style={{ maxWidth: '100%' }}
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
                      <div className="font-bold amount-bss">{formatBsS(sale.totalBsS)}</div>
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
                      className="btn btn-primary w-full flex-align-center justify-center gap-2"
                      onClick={() => setSelectedSaleForCheckout(sale)}
                      style={{ height: '44px', fontSize: '0.95rem' }}
                    >
                      <CheckCircle size={18} /> {remainingUsd <= 0.05 ? 'Cerrar / Entregar Cuenta' : 'Liquidar / Abonar Cuenta'}
                    </button>

                    <div className="d-flex gap-2">
                      <button
                        className="btn btn-outline flex-1 flex-align-center justify-center gap-1"
                        onClick={() => handleEditSale(sale)}
                        style={{ height: '38px' }}
                      >
                        <Edit2 size={16} /> Editar Pedido
                      </button>
                      <button
                        className="btn btn-outline flex-1 flex-align-center justify-center gap-1"
                        onClick={() => toggleExpand(sale.id)}
                        style={{ height: '38px' }}
                      >
                        {isExpanded ? <ChevronDown size={16} /> : <ChevronRight size={16} />} Detalle ({sale.items.length})
                      </button>
                    </div>
                  </div>

                  {/* Mobile Collapsible Detail */}
                  {isExpanded && (
                    <div className="border-top-dashed pt-3" style={{ marginTop: '4px', fontSize: '0.85rem' }}>
                      <h4 style={{ margin: '0 0 8px 0', fontSize: '0.9rem' }}>📦 Productos del Pedido</h4>
                      <div className="d-flex flex-column gap-1" style={{ marginBottom: '14px' }}>
                        {sale.items.map(item => (
                          <div key={item.id} className="d-flex flex-between align-start border-bottom" style={{ padding: '4px 0' }}>
                            <div style={{ minWidth: 0, marginRight: '12px' }}>
                              <div><strong>{item.displayProductName || item.productName}</strong></div>
                              <div className="text-xs text-muted">{formatQuantity(item.quantity)} x {formatBsS(item.unitPriceBsS)}</div>
                            </div>
                            <div className="font-bold amount-bss text-right text-nowrap flex-shrink-0" style={{ marginLeft: 'auto' }}>{formatBsS(item.subtotalBsS)}</div>
                          </div>
                        ))}
                      </div>

                      <h4 style={{ margin: '0 0 8px 0', fontSize: '0.9rem', display: 'flex', alignItems: 'center', gap: '4px' }}>
                        <ShieldCheck size={16} className="color-primary" /> Historial de Abonos
                      </h4>
                      {sale.payments.length === 0 ? (
                        <p className="text-xs text-muted">Sin abonos previos.</p>
                      ) : (
                        <div className="d-flex flex-column gap-1">
                          {sale.payments.map(p => (
                            <div key={p.id} className="d-flex flex-between align-start border-bottom" style={{ padding: '4px 0', fontSize: '0.8rem' }}>
                              <div style={{ minWidth: 0, marginRight: '12px' }}>
                                <span>{p.paymentMethodName}</span>
                                <span className="text-muted ml-2">({formatBsS(p.amountBsS)})</span>
                              </div>
                              <div className="font-bold amount-usd text-right text-nowrap flex-shrink-0" style={{ marginLeft: 'auto' }}>+{formatUSD(p.amount)}</div>
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
        </>
      )}

      {/* Checkout Modal for Completing OnHold Sales */}
      {selectedSaleForCheckout && (
        <CheckoutModal
          isOpen={!!selectedSaleForCheckout}
          onClose={() => setSelectedSaleForCheckout(null)}
          overrideSale={selectedSaleForCheckout}
          onCompleteSale={async (paymentList, roundingAdjustment, isPendingPickup) => {
            try {
              const targetSaleId = selectedSaleForCheckout.id;
              const currentPaidUsd = selectedSaleForCheckout.totalPaidUSD || 0;
              const totalUsd = selectedSaleForCheckout.totalUSD || 0;
              const paidNowUsd = paymentList.reduce((acc, p) => acc + (p.amount || 0), 0);
              const remainingDebtAfterUsd = totalUsd - (currentPaidUsd + paidNowUsd);
              const isFullyCompleted = remainingDebtAfterUsd <= 0.05;

              if (isFullyCompleted) {
                const invoiceNumber = await completeSale(targetSaleId, exchangeRate, paymentList, roundingAdjustment, null, isPendingPickup);
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
                for (const p of paymentList) {
                  await addPaymentToHoldSale(targetSaleId, {
                    paymentMethodId: p.paymentMethodId,
                    amountBsS: p.amountBsS || p.amountLocal,
                    exchangeRate: exchangeRate,
                    referenceNumber: p.referenceNumber || null
                  });
                }
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
              alert(err.response?.data || err.message || 'Error al procesar la operación.');
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
        <Modal isOpen={true} onClose={() => setShowConfirmCancel(false)} title="Confirmar Anulación de Pedido" maxWidth="440px" centerTitle={true}>
          <div className="text-center py-2">
            <div style={{ width: '48px', height: '48px', borderRadius: '50%', backgroundColor: 'rgba(239, 68, 68, 0.15)', color: '#ef4444', display: 'flex', alignItems: 'center', justifyContent: 'center', margin: '0 auto 12px auto' }}>
              <AlertTriangle size={24} />
            </div>
            <h3 className="font-bold text-base mb-2 text-primary">¿Está seguro de que desea anular el Pedido #{selectedSale.id}?</h3>
            <p className="text-xs text-muted mb-4" style={{ lineHeight: '1.5' }}>
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
