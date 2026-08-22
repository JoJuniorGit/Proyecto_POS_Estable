import React, { useState, useEffect, useCallback } from 'react';
import { getPendingPickups, confirmPickup } from '../services/pendingPickupApi';
import { formatBsS, formatUSD, formatQuantity } from '../utils/formatters';
import Modal from '../components/ui/Modal';
import {
  PackageCheck,
  User,
  CheckCircle,
  Loader2,
  AlertCircle,
  ShoppingBag,
  RefreshCw,
  Search,
  ChevronRight,
  ChevronDown
} from 'lucide-react';

export default function PendingPickupsPage() {
  const [pickups, setPickups] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [expandedSaleId, setExpandedSaleId] = useState(null);
  const [selectedPickup, setSelectedPickup] = useState(null);
  const [isConfirming, setIsConfirming] = useState(false);
  const [successMessage, setSuccessMessage] = useState(null);

  const loadPickups = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await getPendingPickups();
      setPickups(data || []);
    } catch (err) {
      console.error('[PendingPickupsPage] Error cargando retiros pendientes:', err);
      setError('No se pudieron cargar los pedidos pendientes por retirar.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadPickups();
    const handlePickupsUpdate = () => loadPickups();
    window.addEventListener('pendingPickupsUpdated', handlePickupsUpdate);
    return () => window.removeEventListener('pendingPickupsUpdated', handlePickupsUpdate);
  }, [loadPickups]);

  const toggleExpand = (id) => {
    setExpandedSaleId((prev) => (prev === id ? null : id));
  };

  const filteredPickups = pickups.filter((item) => {
    if (!searchQuery) return true;
    const q = searchQuery.toLowerCase().trim();
    const inv = (item.invoiceNumber || item.saleId || '').toString();
    const name = (item.customerName || '').toLowerCase();
    const cedula = (item.customerCedula || '').toLowerCase();
    return inv.includes(q) || name.includes(q) || cedula.includes(q);
  });

  const handleConfirmPickupClick = (pickup) => {
    setSelectedPickup(pickup);
  };

  const handleExecutePickup = async () => {
    if (!selectedPickup) return;
    setIsConfirming(true);
    try {
      await confirmPickup(selectedPickup.saleId);
      setSuccessMessage(`¡Retiro confirmado con éxito para la Factura N° ${selectedPickup.invoiceNumber || selectedPickup.saleId}!`);
      setSelectedPickup(null);
      await loadPickups();
    } catch (err) {
      console.error('[PendingPickupsPage] Error al confirmar retiro:', err);
      setError(err.message || 'Ocurrió un error al confirmar la entrega.');
    } finally {
      setIsConfirming(false);
    }
  };

  return (
    <div className="pending-orders-container">
      {/* ── 1. Encabezado Adaptativo (Mismo layout que Cuentas Abiertas) ── */}
      <div className="pending-orders-header">
        <div>
          <h1 className="pending-orders-title">
            <PackageCheck className="color-primary" size={26} /> Retiros Pendientes (Mercancía en Custodia)
          </h1>
          <p className="pending-orders-desc">
            Gestión de mercancía pagada al 100% resguardada en el local pendiente por entrega física al cliente.
          </p>
        </div>

        <button className="btn btn-outline flex-align-center gap-2" onClick={loadPickups} disabled={loading}>
          <RefreshCw size={16} className={loading ? 'spin' : ''} /> Refrescar
        </button>
      </div>

      {/* ── 2. Controles de Búsqueda e Indicador ── */}
      <div className="pending-orders-controls-bar">
        <div className="pending-orders-search-wrapper" style={{ height: '42px' }}>
          <input
            type="text"
            className="input-field"
            placeholder="Buscar por N° de Factura, Nombre o Cédula/RIF..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            style={{ width: '100%', height: '42px', paddingLeft: '36px', boxSizing: 'border-box' }}
          />
          <Search size={18} style={{ position: 'absolute', left: '10px', top: '50%', transform: 'translateY(-50%)', opacity: 0.5 }} />
        </div>

        <div className="pending-orders-bcv-badge" style={{ height: '42px', display: 'inline-flex', alignItems: 'center', boxSizing: 'border-box', margin: 0 }}>
          Mercancía en Custodia: <span style={{ color: 'var(--primary-color, #818cf8)', fontWeight: 700, marginLeft: '6px' }}>{filteredPickups.length} {filteredPickups.length === 1 ? 'Pedido' : 'Pedidos'}</span>
        </div>
      </div>

      {successMessage && (
        <div className="alert-box success-alert mb-4 flex-align-center gap-2" style={{ display: 'flex', alignItems: 'center', gap: '8px', padding: '12px 16px', borderRadius: '8px', backgroundColor: 'rgba(16, 185, 129, 0.12)', border: '1px solid #10b981', color: '#10b981' }}>
          <CheckCircle size={20} className="flex-shrink-0" />
          <span>{successMessage}</span>
          <button
            type="button"
            className="btn btn-sm btn-link text-success ml-auto"
            onClick={() => setSuccessMessage(null)}
            style={{ marginLeft: 'auto', color: '#10b981', background: 'none', border: 'none', cursor: 'pointer', fontWeight: 'bold' }}
          >
            Aceptar
          </button>
        </div>
      )}

      {error && (
        <div className="alert-box danger-alert mb-4">
          <AlertCircle size={20} className="flex-shrink-0" />
          <span>{error}</span>
        </div>
      )}

      {loading && pickups.length === 0 ? (
        <div style={{ padding: '60px', textAlign: 'center' }}>
          <Loader2 size={40} className="spin color-primary" style={{ margin: '0 auto 16px auto' }} />
          <p>Cargando pedidos pendientes por retirar...</p>
        </div>
      ) : filteredPickups.length === 0 ? (
        <div className="card p-5 text-center text-muted" style={{ padding: '40px', textAlign: 'center' }}>
          <ShoppingBag size={48} className="mx-auto mb-3 text-muted" style={{ opacity: 0.5, margin: '0 auto 12px auto' }} />
          <h3 className="font-bold text-lg mb-1" style={{ fontSize: '1.2rem', fontWeight: 700 }}>No hay mercancía pendiente por retirar</h3>
          <p className="text-sm" style={{ opacity: 0.7 }}>
            {searchQuery ? 'No se encontraron pedidos que coincidan con la búsqueda.' : 'Todos los apartados pagados han sido entregados a sus respectivos clientes.'}
          </p>
        </div>
      ) : (
        <>
          {/* ── 3A. VISTA ESCRITORIO (TABLA TRADICIONAL ESTRUCTURA CUENTAS ABIERTAS) ── */}
          <div className="pending-desktop-view dark-card" style={{ overflow: 'hidden' }}>
            <table style={{ width: '100%', borderCollapse: 'collapse', textAlign: 'left' }}>
              <thead>
                <tr style={{ borderBottom: '1px solid var(--border-color)', backgroundColor: 'var(--bg-secondary, rgba(255,255,255,0.03))' }}>
                  <th style={{ padding: '14px 16px' }}>Factura N° / Fecha</th>
                  <th style={{ padding: '14px 16px' }}>Cliente</th>
                  <th style={{ padding: '14px 16px', textAlign: 'right' }}>TOTAL FACTURA (Bs.S)</th>
                  <th style={{ padding: '14px 16px', textAlign: 'center' }}>Estado</th>
                  <th style={{ padding: '14px 16px', textAlign: 'right' }}>Acciones</th>
                </tr>
              </thead>
              <tbody>
                {filteredPickups.map((pickup) => {
                  const isExpanded = expandedSaleId === pickup.saleId;

                  return (
                    <React.Fragment key={pickup.saleId}>
                      <tr
                        style={{
                          borderBottom: '1px solid var(--border-color)',
                          cursor: 'pointer',
                          backgroundColor: isExpanded ? 'var(--bg-secondary, rgba(255,255,255,0.05))' : 'transparent',
                        }}
                        onClick={() => toggleExpand(pickup.saleId)}
                      >
                        <td style={{ padding: '14px 16px' }}>
                          <div style={{ display: 'flex', alignItems: 'center' }}>
                            {isExpanded ? (
                              <ChevronDown size={18} style={{ marginRight: '12px', flexShrink: 0, color: 'var(--primary-color, #818cf8)' }} />
                            ) : (
                              <ChevronRight size={18} style={{ marginRight: '12px', flexShrink: 0, opacity: 0.6 }} />
                            )}
                            <div>
                              <strong>Factura N° {pickup.invoiceNumber || pickup.saleId}</strong>
                              <div style={{ fontSize: '0.78rem', opacity: 0.6, marginTop: '2px' }}>
                                {new Date(pickup.date).toLocaleDateString()} {new Date(pickup.date).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                              </div>
                            </div>
                          </div>
                        </td>

                        <td style={{ padding: '14px 16px', maxWidth: '240px' }}>
                          <div
                            className="font-medium text-truncate"
                            title={pickup.customerName || 'Consumidor Final'}
                            style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: '240px' }}
                          >
                            <strong>{pickup.customerName || 'Consumidor Final'}</strong>
                          </div>
                          <div className="text-muted text-truncate" style={{ fontSize: '0.78rem', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', opacity: 0.7, marginTop: '2px' }}>
                            RIF/Cédula: {pickup.customerCedula || 'N/A'}
                          </div>
                          {pickup.customerPhone && (
                            <div className="text-muted text-truncate" style={{ fontSize: '0.78rem', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', opacity: 0.7 }}>
                              Tel: {pickup.customerPhone}
                            </div>
                          )}
                        </td>

                        <td style={{ padding: '14px 16px', textAlign: 'right' }}>
                          <strong className="font-mono" style={{ fontSize: '0.95rem', display: 'block', textAlign: 'right' }}>
                            {formatBsS(pickup.totalBsS).replace(/^Bs\.S\s?/, '')}
                          </strong>
                          <div style={{ fontSize: '0.75rem', color: 'var(--text-muted)', fontWeight: 400, marginTop: '2px', textAlign: 'right' }}>
                            {formatUSD(pickup.totalUSD || 0)}
                          </div>
                        </td>

                        <td style={{ padding: '14px 16px', textAlign: 'center' }}>
                          <span className="badge badge-warning" style={{ backgroundColor: 'rgba(245, 158, 11, 0.15)', color: '#f59e0b', padding: '4px 12px', borderRadius: '12px', fontSize: '0.78rem', fontWeight: 600 }}>
                            En Custodia
                          </span>
                        </td>

                        <td style={{ padding: '14px 16px', textAlign: 'right' }} onClick={(e) => e.stopPropagation()}>
                          <div style={{ display: 'flex', gap: '8px', justifyContent: 'flex-end' }}>
                            <button
                              type="button"
                              className="btn btn-sm btn-outline flex-align-center gap-1"
                              onClick={() => handleConfirmPickupClick(pickup)}
                              style={{
                                display: 'inline-flex',
                                alignItems: 'center',
                                gap: '6px',
                                color: '#818cf8',
                                borderColor: 'rgba(99, 102, 241, 0.4)',
                                backgroundColor: 'rgba(99, 102, 241, 0.08)',
                                fontWeight: 600,
                                fontSize: '0.82rem',
                                padding: '6px 12px',
                                borderRadius: '6px',
                                transition: 'all 0.2s ease'
                              }}
                            >
                              <PackageCheck size={15} /> Confirmar Retiro
                            </button>
                          </div>
                        </td>
                      </tr>

                      {/* Expanded Detail Desktop */}
                      {isExpanded && (
                        <tr className="history-detail-row">
                          <td colSpan="6" className="history-detail-cell" style={{ padding: '20px', backgroundColor: 'var(--bg-secondary, rgba(0,0,0,0.2))' }}>
                            <div>
                              <h4 style={{ margin: '0 0 10px 0', fontSize: '0.95rem' }}>📦 Productos del Pedido a Entregar ({pickup.items?.length || 0})</h4>
                              <table style={{ width: '100%', fontSize: '0.9em', borderCollapse: 'collapse' }}>
                                <thead>
                                  <tr style={{ borderBottom: '1px solid var(--border-color)', opacity: 0.7 }}>
                                    <th style={{ textAlign: 'left', padding: '8px 12px 8px 0', width: 'auto' }}>Producto</th>
                                    <th style={{ textAlign: 'right', padding: '8px 12px', width: '80px', whiteSpace: 'nowrap' }}>Cant.</th>
                                    <th style={{ textAlign: 'right', padding: '8px 12px', width: '160px', whiteSpace: 'nowrap' }}>P. Unit Bs.S</th>
                                    <th style={{ textAlign: 'right', padding: '8px 12px', width: '180px', whiteSpace: 'nowrap' }}>Subtotal Bs.S</th>
                                  </tr>
                                </thead>
                                <tbody>
                                  {(pickup.items || []).map((item, idx) => (
                                    <tr key={idx} style={{ borderBottom: '1px dashed var(--border-color)' }}>
                                      <td style={{ padding: '6px 12px 6px 0', fontWeight: 500 }}>{item.productName}</td>
                                      <td style={{ textAlign: 'right', padding: '6px 12px', fontWeight: 700, whiteSpace: 'nowrap' }}>{formatQuantity(item.quantity)}</td>
                                      <td style={{ textAlign: 'right', padding: '6px 12px', fontFamily: 'monospace', whiteSpace: 'nowrap' }}>{formatBsS(item.unitPriceBsS)}</td>
                                      <td style={{ textAlign: 'right', padding: '6px 12px', fontFamily: 'monospace', fontWeight: 700, whiteSpace: 'nowrap' }}>{formatBsS(item.subtotalBsS)}</td>
                                    </tr>
                                  ))}
                                </tbody>
                              </table>
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

          {/* ── 3B. VISTA MÓVIL ── */}
          <div className="pending-mobile-view">
            {filteredPickups.map((pickup) => {
              const isExpanded = expandedSaleId === pickup.saleId;

              return (
                <div key={pickup.saleId} className="pending-mobile-card">
                  {/* Card Header */}
                  <div className="pending-mobile-card-header">
                    <div>
                      <div className="font-bold text-base flex-align-center gap-1" onClick={() => toggleExpand(pickup.saleId)} style={{ cursor: 'pointer', display: 'flex', alignItems: 'center', gap: '4px' }}>
                        {isExpanded ? <ChevronDown size={18} style={{ marginRight: '4px' }} /> : <ChevronRight size={18} style={{ marginRight: '4px' }} />}
                        Factura N° {pickup.invoiceNumber || pickup.saleId}
                      </div>
                      <div className="text-xs text-muted mt-1">
                        {new Date(pickup.date).toLocaleDateString()} {new Date(pickup.date).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                      </div>
                    </div>

                    <span className="badge badge-warning" style={{ backgroundColor: 'rgba(245, 158, 11, 0.15)', color: '#f59e0b', padding: '4px 10px', borderRadius: '12px', fontSize: '0.78rem', fontWeight: 600 }}>
                      En Custodia
                    </span>
                  </div>

                  {/* Customer Info Box */}
                  <div className="pending-mobile-card-customer">
                    <div className="flex-align-center gap-1 font-bold pending-mobile-card-customer-name" style={{ overflow: 'hidden', minWidth: 0, display: 'flex', alignItems: 'center', gap: '6px' }}>
                      <User size={15} className="text-muted flex-shrink-0" />
                      <span
                        title={pickup.customerName || 'Consumidor Final'}
                        style={{
                          overflow: 'hidden',
                          textOverflow: 'ellipsis',
                          whiteSpace: 'nowrap',
                          display: 'inline-block',
                          maxWidth: '100%'
                        }}
                      >
                        {pickup.customerName || 'Consumidor Final'}
                      </span>
                    </div>
                    <div className="text-xs text-muted mt-1 ml-4" style={{ display: 'flex', flexDirection: 'column', gap: '2px' }}>
                      <span>RIF/Cédula: <strong>{pickup.customerCedula || 'N/A'}</strong></span>
                      {pickup.customerPhone && <span>Tel: <strong>{pickup.customerPhone}</strong></span>}
                    </div>
                  </div>

                  {/* Financial Breakdown Grid */}
                  <div
                    className="pending-mobile-card-summary"
                    style={{ gridTemplateColumns: '1fr 1fr', gap: '12px' }}
                  >
                    <div>
                      <div className="text-xs text-muted mb-1">Total (USD)</div>
                      <div className="font-bold" style={{ fontSize: '1.1rem', color: 'var(--text-primary)', whiteSpace: 'nowrap' }}>{formatUSD(pickup.totalUSD)}</div>
                    </div>
                    <div style={{ textAlign: 'right' }}>
                      <div className="text-xs text-muted mb-1">Total (Bs.S)</div>
                      <div className="font-bold font-mono" style={{ fontSize: '1.1rem', color: 'var(--text-primary)', whiteSpace: 'nowrap' }}>{formatBsS(pickup.totalBsS).replace(/^Bs\.S\s?/, '')}</div>
                    </div>
                  </div>

                  {/* Touch-friendly Action Buttons */}
                  <div className="pending-mobile-card-actions">
                    <button
                      type="button"
                      className="btn btn-outline flex-1 flex-align-center justify-center gap-1"
                      onClick={() => handleConfirmPickupClick(pickup)}
                      style={{
                        height: '42px',
                        fontSize: '0.9rem',
                        display: 'flex',
                        alignItems: 'center',
                        justifyContent: 'center',
                        gap: '8px',
                        color: '#818cf8',
                        borderColor: 'rgba(99, 102, 241, 0.4)',
                        backgroundColor: 'rgba(99, 102, 241, 0.08)',
                        fontWeight: 600
                      }}
                    >
                      <PackageCheck size={18} /> Confirmar Retiro
                    </button>

                    <button
                      type="button"
                      className="btn btn-outline flex-1 flex-align-center justify-center gap-1"
                      onClick={() => toggleExpand(pickup.saleId)}
                      style={{ height: '38px', width: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', gap: '6px' }}
                    >
                      {isExpanded ? <ChevronDown size={16} /> : <ChevronRight size={16} />} Detalle ({pickup.items?.length || 0} productos)
                    </button>
                  </div>

                  {/* Mobile Collapsible Detail */}
                  {isExpanded && (
                    <div style={{ borderTop: '1px dashed var(--border-color)', paddingTop: '12px', marginTop: '4px', fontSize: '0.85rem' }}>
                      <h4 style={{ margin: '0 0 8px 0', fontSize: '0.9rem' }}>📦 Productos del Pedido</h4>
                      <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                        {(pickup.items || []).map((item, idx) => (
                          <div key={idx} style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', padding: '6px 0', borderBottom: '1px solid var(--border-color)' }}>
                            <div style={{ flex: '1 1 auto', minWidth: 0, paddingRight: '12px' }}>
                              <div><strong>{item.productName}</strong></div>
                              <div className="text-xs text-muted">{formatQuantity(item.quantity)} unds x {formatBsS(item.unitPriceBsS)}</div>
                            </div>
                            <div className="font-bold font-mono" style={{ textAlign: 'right', whiteSpace: 'nowrap', flexShrink: 0 }}>{formatBsS(item.subtotalBsS)}</div>
                          </div>
                        ))}
                      </div>
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        </>
      )}

      {/* Modal de Confirmación de Retiro */}
      {selectedPickup && (
        <Modal
          isOpen={Boolean(selectedPickup)}
          onClose={() => setSelectedPickup(null)}
          title="Confirmar Entrega de Mercancía"
          maxWidth="480px"
          centerTitle={false}
        >
          <div className="p-2 text-center" style={{ padding: '10px 4px', textAlign: 'center' }}>
            <div style={{
              width: '56px',
              height: '56px',
              borderRadius: '50%',
              backgroundColor: 'rgba(99, 102, 241, 0.15)',
              color: 'var(--primary-color, #818cf8)',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              margin: '0 auto 16px auto'
            }}>
              <PackageCheck size={28} />
            </div>

            <h4 className="font-bold mb-2 text-primary" style={{ fontSize: '1.15rem', fontWeight: 700, lineHeight: 1.4 }}>
              ¿Entregar pedido a <span style={{ color: '#ffffff', fontWeight: 700 }}>{selectedPickup.customerName || 'Consumidor Final'}</span>?
            </h4>
            <p className="text-muted text-sm mb-4" style={{ opacity: 0.85, marginBottom: '24px', lineHeight: 1.5 }}>
              Se registrará la salida física de la mercancía correspondiente a la <span style={{ color: 'var(--primary-color, #818cf8)', fontWeight: 700 }}>Factura N° {selectedPickup.invoiceNumber || selectedPickup.saleId}</span>.
            </p>

            <div style={{ display: 'flex', justifyContent: 'center', gap: '12px', paddingTop: '16px', borderTop: '1px solid var(--border-color)' }}>
              <button
                type="button"
                className="btn btn-outline"
                onClick={() => setSelectedPickup(null)}
                disabled={isConfirming}
                style={{ minWidth: '110px' }}
              >
                Cancelar
              </button>
              <button
                type="button"
                className="btn btn-primary"
                onClick={handleExecutePickup}
                disabled={isConfirming}
                style={{ display: 'inline-flex', alignItems: 'center', justifyContent: 'center', gap: '8px', minWidth: '170px' }}
              >
                {isConfirming ? (
                  <>
                    <Loader2 className="animate-spin" size={18} /> Procesando...
                  </>
                ) : (
                  <>
                    <CheckCircle size={18} /> Confirmar Entrega
                  </>
                )}
              </button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}
