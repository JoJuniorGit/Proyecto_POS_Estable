import React, { useState, useEffect, useCallback } from 'react';
import { getPendingSales, completeSale, addPaymentToHoldSale } from '../services/salesApi';
import { getActivePaymentMethods } from '../services/paymentApi';
import { useExchangeRate } from '../context/ExchangeRateContext';
import { useCart } from '../context/CartContext';
import CheckoutModal from '../components/checkout/CheckoutModal';
import EditSaleModal from '../components/pos/EditSaleModal';
import SuccessScreen from '../components/checkout/SuccessScreen';
import { formatNumberEs, formatBsS, formatUSD } from '../utils/formatters';
import { Search, Loader2, Clock, ChevronRight, ChevronDown, RefreshCw, CheckCircle, ShieldCheck, Edit2, User } from 'lucide-react';

export default function PendingOrdersPage({ onNavigate }) {
  const [sales, setSales] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [expandedSaleId, setExpandedSaleId] = useState(null);
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
  }, [loadPendingData]);

  const toggleExpand = (id) => {
    setExpandedSaleId(prev => prev === id ? null : id);
  };

  const handleEditSale = (sale) => {
    setSelectedSaleForEdit(sale);
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

        <button className="btn btn-outline flex-align-center gap-2" onClick={loadPendingData} disabled={loading}>
          <RefreshCw size={16} className={loading ? 'spin' : ''} /> Actualizar
        </button>
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
          Tasa BCV del Día: <span style={{ color: 'var(--primary-color, #6366f1)' }}>{formatNumberEs(exchangeRate)} Bs/$</span>
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
          <div className="pending-desktop-view dark-card" style={{ overflow: 'hidden' }}>
            <table style={{ width: '100%', borderCollapse: 'collapse', textAlign: 'left' }}>
              <thead>
                <tr style={{ borderBottom: '1px solid var(--border-color)', backgroundColor: 'var(--bg-secondary, rgba(255,255,255,0.03))' }}>
                  <th style={{ padding: '14px' }}>ID / Fecha</th>
                  <th style={{ padding: '14px' }}>Cliente</th>
                  <th style={{ padding: '14px' }}>Total Factura</th>
                  <th style={{ padding: '14px' }}>Estado</th>
                  <th style={{ padding: '14px', textAlign: 'right' }}>Acciones</th>
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
                          borderBottom: '1px solid var(--border-color)',
                          cursor: 'pointer',
                          backgroundColor: isExpanded ? 'var(--bg-secondary, rgba(255,255,255,0.05))' : 'transparent',
                        }}
                        onClick={() => toggleExpand(sale.id)}
                      >
                        <td style={{ padding: '14px' }}>
                          <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                            {isExpanded ? <ChevronDown size={18} /> : <ChevronRight size={18} />}
                            <div>
                              <strong>Pedido #{sale.id}</strong>
                              <div style={{ fontSize: '0.8em', opacity: 0.6 }}>
                                {new Date(sale.date).toLocaleDateString()} {new Date(sale.date).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                              </div>
                            </div>
                          </div>
                        </td>

                        <td style={{ padding: '14px', maxWidth: '220px' }}>
                          <div 
                            className="font-medium"
                            title={sale.customerName || sale.customer?.name || 'Consumidor Final'}
                            style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: '220px' }}
                          >
                            <strong>{sale.customerName || sale.customer?.name || 'Consumidor Final'}</strong>
                          </div>
                          <div style={{ fontSize: '0.8em', opacity: 0.6 }}>{sale.customerCedula || sale.customer?.cedulaOrRif || 'V-00000000'}</div>
                        </td>

                        <td style={{ padding: '14px' }}>
                          <div style={{ fontWeight: 'bold', fontSize: '0.95rem', whiteSpace: 'nowrap' }}>
                            {formatBsS(sale.totalBsS)}
                          </div>
                          <div style={{ fontSize: '0.8em', opacity: 0.6 }}>
                            {formatUSD(sale.totalUSD)}
                          </div>
                        </td>

                        <td style={{ padding: '14px' }}>
                          <span className="badge badge-warning" style={{ backgroundColor: 'rgba(245, 158, 11, 0.2)', color: '#f59e0b', padding: '4px 10px', borderRadius: '12px', fontSize: '0.85em' }}>
                            En Espera
                          </span>
                        </td>

                        <td style={{ padding: '14px', textAlign: 'right' }} onClick={(e) => e.stopPropagation()}>
                          <div style={{ display: 'flex', gap: '8px', justifyContent: 'flex-end' }}>
                            <button
                              className="btn btn-sm btn-primary"
                              onClick={() => setSelectedSaleForCheckout(sale)}
                              style={{ display: 'inline-flex', alignItems: 'center', gap: '4px' }}
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
                          <td colSpan="5" className="history-detail-cell" style={{ padding: '20px', backgroundColor: 'var(--bg-secondary, rgba(0,0,0,0.2))' }}>
                            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '20px' }}>
                              
                              {/* Products Section */}
                              <div>
                                <h4 style={{ margin: '0 0 10px 0' }}>📦 Productos del Pedido</h4>
                                <table style={{ width: '100%', fontSize: '0.9em', borderCollapse: 'collapse' }}>
                                  <thead>
                                    <tr style={{ borderBottom: '1px solid var(--border-color)', opacity: 0.7 }}>
                                      <th>Producto</th>
                                      <th>Cant.</th>
                                      <th>P. Unit ($)</th>
                                      <th>Subtotal ($)</th>
                                    </tr>
                                  </thead>
                                  <tbody>
                                    {sale.items.map(item => (
                                      <tr key={item.id} style={{ borderBottom: '1px dashed var(--border-color)' }}>
                                        <td style={{ padding: '6px 0' }}>{item.displayProductName || (item.unitOfMeasure && item.unitOfMeasure !== 'Und' ? `${item.productName} (${item.unitOfMeasure})` : item.productName)}</td>
                                        <td>{item.isFractional ? item.quantity.toFixed(3) : item.quantity}</td>
                                        <td>{formatUSD(item.unitPrice)}</td>
                                        <td>{formatUSD(item.subtotal)}</td>
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
                                      <tr style={{ borderBottom: '1px solid var(--border-color)', opacity: 0.7 }}>
                                        <th>Fecha</th>
                                        <th>Método</th>
                                        <th>Monto Bs.S</th>
                                        <th>Tasa Usada</th>
                                        <th>Abono USD</th>
                                      </tr>
                                    </thead>
                                    <tbody>
                                      {sale.payments.map(p => (
                                        <tr key={p.id} style={{ borderBottom: '1px dashed var(--border-color)' }}>
                                          <td style={{ padding: '6px 0' }}>{new Date(p.createdAt || sale.date).toLocaleDateString()}</td>
                                          <td>{p.paymentMethodName}</td>
                                          <td>{formatBsS(p.amountBsS)}</td>
                                          <td>{formatNumberEs(p.exchangeRate)} Bs/$</td>
                                          <td style={{ fontWeight: 'bold', color: '#10b981' }}>+{formatUSD(p.amount)}</td>
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
              const remainingBsS = remainingUsd * exchangeRate;

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
                    <div className="flex-align-center gap-1 font-bold pending-mobile-card-customer-name" style={{ overflow: 'hidden', minWidth: 0, display: 'flex', alignItems: 'center' }}>
                      <User size={15} className="text-muted flex-shrink-0" />
                      <span
                        title={sale.customerName || sale.customer?.name || 'Consumidor Final'}
                        style={{
                          overflow: 'hidden',
                          textOverflow: 'ellipsis',
                          whiteSpace: 'nowrap',
                          display: 'inline-block',
                          maxWidth: '100%'
                        }}
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
                      <div className="font-bold">{formatBsS(sale.totalBsS)}</div>
                      <div className="text-xs text-muted">{formatUSD(sale.totalUSD)}</div>
                    </div>
                    <div>
                      <div className="text-xs text-muted mb-1">Abonado</div>
                      <div className="font-bold text-success">+{formatUSD(totalPaidUsd)}</div>
                    </div>
                    <div>
                      <div className="text-xs text-muted mb-1">Deuda Pendiente</div>
                      <div className="font-bold text-danger">{formatUSD(remainingUsd)}</div>
                      <div className="text-xs text-muted">≈ {formatBsS(remainingBsS)}</div>
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

                    <div style={{ display: 'flex', gap: '8px' }}>
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
                    <div style={{ borderTop: '1px dashed var(--border-color)', paddingTop: '12px', marginTop: '4px', fontSize: '0.85rem' }}>
                      <h4 style={{ margin: '0 0 8px 0', fontSize: '0.9rem' }}>📦 Productos del Pedido</h4>
                      <div style={{ display: 'flex', flexDirection: 'column', gap: '6px', marginBottom: '14px' }}>
                        {sale.items.map(item => (
                          <div key={item.id} style={{ display: 'flex', justifyContent: 'space-between', padding: '4px 0', borderBottom: '1px solid var(--border-color)' }}>
                            <div>
                              <div><strong>{item.displayProductName || item.productName}</strong></div>
                              <div className="text-xs text-muted">{item.isFractional ? item.quantity.toFixed(3) : item.quantity} x {formatUSD(item.unitPrice)}</div>
                            </div>
                            <div className="font-bold">{formatUSD(item.subtotal)}</div>
                          </div>
                        ))}
                      </div>

                      <h4 style={{ margin: '0 0 8px 0', fontSize: '0.9rem', display: 'flex', alignItems: 'center', gap: '4px' }}>
                        <ShieldCheck size={16} className="color-primary" /> Historial de Abonos
                      </h4>
                      {sale.payments.length === 0 ? (
                        <p className="text-xs text-muted">Sin abonos previos.</p>
                      ) : (
                        <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                          {sale.payments.map(p => (
                            <div key={p.id} style={{ display: 'flex', justifyContent: 'space-between', padding: '4px 0', borderBottom: '1px solid var(--border-color)', fontSize: '0.8rem' }}>
                              <div>
                                <span>{p.paymentMethodName}</span>
                                <span className="text-muted ml-2">({formatBsS(p.amountBsS)})</span>
                              </div>
                              <div className="font-bold text-success">+{formatUSD(p.amount)}</div>
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
          onCompleteSale={async (paymentList, roundingAdjustment) => {
            try {
              const targetSaleId = selectedSaleForCheckout.id;
              const currentPaidUsd = selectedSaleForCheckout.totalPaidUSD || 0;
              const totalUsd = selectedSaleForCheckout.totalUSD || 0;
              const paidNowUsd = paymentList.reduce((acc, p) => acc + (p.amount || 0), 0);
              const remainingDebtAfterUsd = totalUsd - (currentPaidUsd + paidNowUsd);
              const isFullyCompleted = remainingDebtAfterUsd <= 0.05;

              if (isFullyCompleted) {
                const invoiceNumber = await completeSale(targetSaleId, exchangeRate, paymentList, roundingAdjustment);
                setSelectedSaleForCheckout(null);
                await loadPendingData();

                setCompletedLiquidation({
                  invoiceNumber: invoiceNumber > 0 ? invoiceNumber : targetSaleId,
                  title: "¡Cuenta Liquidada con Éxito!",
                  badgeText: `Factura N° ${invoiceNumber > 0 ? invoiceNumber.toString().padStart(6, '0') : targetSaleId}`,
                  message: "La factura fue completada en su totalidad y el inventario ha sido descontado correctamente.",
                  buttonText: "Aceptar"
                });
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
    </div>
  );
}
