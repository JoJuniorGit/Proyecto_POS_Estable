import React, { useState, useEffect, useCallback } from 'react';
import { getPendingPickupsPage, confirmPickup } from '../services/pendingPickupApi';
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
import './PendingPickupsPage.css';

export default function PendingPickupsPage() {
  const [pickups, setPickups] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [expandedSaleId, setExpandedSaleId] = useState(null);
  const [selectedPickup, setSelectedPickup] = useState(null);
  const [isConfirming, setIsConfirming] = useState(false);
  const [successMessage, setSuccessMessage] = useState(null);

  // 8.14-N1: paginación de UI — página actual (offset) y si hay más para el botón "Ver más".
  const PAGE_SIZE = 200;
  const [hasMore, setHasMore] = useState(false);
  const [pageOffset, setPageOffset] = useState(0);

  const loadPickups = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const { items, totalCount } = await getPendingPickupsPage({ limit: PAGE_SIZE, offset: 0 });
      setPickups(items || []);
      setPageOffset(items?.length || 0);
      setHasMore(totalCount > (items?.length || 0));
    } catch (err) {
      console.error('[PendingPickupsPage] Error cargando retiros pendientes:', err);
      setError('No se pudieron cargar los pedidos pendientes por retirar.');
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
      const { items, totalCount } = await getPendingPickupsPage({ limit: PAGE_SIZE, offset: pageOffset });
      if (items.length > 0) {
        setPickups(prev => [...(prev || []), ...items]);
        setPageOffset(prev => prev + items.length);
      }
      setHasMore(totalCount > pageOffset + items.length);
    } catch (err) {
      console.error('[PendingPickupsPage] Error cargando más retiros:', err);
      setError('No se pudieron cargar más pedidos pendientes.');
    } finally {
      setLoading(false);
    }
  }, [loading, pageOffset]);

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
        <div className="pending-orders-search-wrapper ppk-search-wrapper">
          <input
            type="text"
            className="input-field w-full ppk-search-input"
            placeholder="Buscar por N° de Factura, Nombre o Cédula/RIF..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
          />
          <Search size={18} className="ppk-search-icon opacity-50" />
        </div>

        <div className="pending-orders-bcv-badge ppk-bcv-badge">
          Mercancía en Custodia: <span className="ppk-bcv-count">{filteredPickups.length} {filteredPickups.length === 1 ? 'Pedido' : 'Pedidos'}</span>
        </div>
      </div>

      {successMessage && (
        <div className="alert-box success-alert mb-4 flex-align-center gap-2 ppk-success-alert">
          <CheckCircle size={20} className="flex-shrink-0" />
          <span>{successMessage}</span>
          <button
            type="button"
            className="btn btn-sm btn-link text-success ml-auto ppk-alert-close"
            onClick={() => setSuccessMessage(null)}
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
        <div className="ppk-loading text-center">
          <Loader2 size={40} className="spin color-primary mb-4 mx-auto" />
          <p>Cargando pedidos pendientes por retirar...</p>
        </div>
      ) : filteredPickups.length === 0 ? (
        <div className="card p-5 text-center text-muted ppk-empty-card">
          <ShoppingBag size={48} className="mx-auto mb-3 text-muted opacity-50" />
          <h3 className="font-bold text-lg mb-1 ppk-empty-title">No hay mercancía pendiente por retirar</h3>
          <p className="text-sm ppk-faded">
            {searchQuery ? 'No se encontraron pedidos que coincidan con la búsqueda.' : 'Todos los apartados pagados han sido entregados a sus respectivos clientes.'}
          </p>
        </div>
      ) : (
        <>
          {/* ── 3A. VISTA ESCRITORIO (TABLA TRADICIONAL ESTRUCTURA CUENTAS ABIERTAS) ── */}
          <div className="pending-desktop-view dark-card overflow-hidden">
            <table className="ppk-table">
              <thead>
                <tr className="ppk-border-bottom ppk-thead-bg">
                  <th className="ppk-table-pad">Factura N° / Fecha</th>
                  <th className="ppk-table-pad">Cliente</th>
                  <th className="ppk-table-pad text-right">TOTAL FACTURA (Bs.S)</th>
                  <th className="ppk-table-pad text-center">Estado</th>
                  <th className="ppk-table-pad text-right">Acciones</th>
                </tr>
              </thead>
              <tbody>
                {filteredPickups.map((pickup) => {
                  const isExpanded = expandedSaleId === pickup.saleId;

                  return (
                    <React.Fragment key={pickup.saleId}>
                      <tr
                        className="ppk-border-bottom cursor-pointer"
                        style={{
                          backgroundColor: isExpanded ? 'var(--bg-secondary, rgba(255,255,255,0.05))' : 'transparent',
                        }}
                        onClick={() => toggleExpand(pickup.saleId)}
                      >
                        <td className="ppk-table-pad">
                          <div className="flex-align-center">
                            {isExpanded ? (
                              <ChevronDown size={18} className="ppk-caret ppk-caret-active" />
                            ) : (
                              <ChevronRight size={18} className="ppk-caret ppk-caret-inactive" />
                            )}
                            <div>
                              <strong>Factura N° {pickup.invoiceNumber || pickup.saleId}</strong>
                              <div className="ppk-invoice-date">
                                {new Date(pickup.date).toLocaleDateString()} {new Date(pickup.date).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                              </div>
                            </div>
                          </div>
                        </td>

                        <td className="ppk-table-pad ppk-maxw240">
                          <div
                            className="font-medium text-truncate ppk-maxw240"
                            title={pickup.customerName || 'Consumidor Final'}
                          >
                            <strong>{pickup.customerName || 'Consumidor Final'}</strong>
                          </div>
                          <div className="text-muted text-truncate ppk-customer-meta ppk-mt-2px">
                            RIF/Cédula: {pickup.customerCedula || 'N/A'}
                          </div>
                          {pickup.customerPhone && (
                            <div className="text-muted text-truncate ppk-customer-meta">
                              Tel: {pickup.customerPhone}
                            </div>
                          )}
                        </td>

                        <td className="ppk-table-pad text-right">
                          <strong className="font-mono ppk-total-mono">
                            {formatBsS(pickup.totalBsS).replace(/^Bs\.S\s?/, '')}
                          </strong>
                          <div className="text-muted font-normal text-xs text-right ppk-mt-2px">
                            {formatUSD(pickup.totalUSD || 0)}
                          </div>
                        </td>

                        <td className="ppk-table-pad text-center">
                          <span className="badge badge-warning ppk-custody-badge ppk-custody-badge-xl">
                            En Custodia
                          </span>
                        </td>

                        <td className="ppk-table-pad text-right" onClick={(e) => e.stopPropagation()}>
                          <div className="d-flex gap-2 justify-end">
                            <button
                              type="button"
                              className="btn btn-sm btn-outline flex-align-center gap-1 ppk-confirm-btn"
                              onClick={() => handleConfirmPickupClick(pickup)}
                            >
                              <PackageCheck size={15} /> Confirmar Retiro
                            </button>
                          </div>
                        </td>
                      </tr>

                      {/* Expanded Detail Desktop */}
                      {isExpanded && (
                        <tr className="history-detail-row">
                          <td colSpan="6" className="history-detail-cell ppk-detail-cell">
                            <div>
                              <h4 className="ppk-detail-h4">📦 Productos del Pedido a Entregar ({pickup.items?.length || 0})</h4>
                              <table className="w-full ppk-detail-table">
                                <thead>
                                  <tr className="ppk-border-bottom ppk-faded">
                                    <th className="ppk-detail-th-first">Producto</th>
                                    <th className="ppk-detail-th-mid ppk-th-cant">Cant.</th>
                                    <th className="ppk-detail-th-mid ppk-th-punit">P. Unit Bs.S</th>
                                    <th className="ppk-detail-th-mid ppk-th-subtotal">Subtotal Bs.S</th>
                                  </tr>
                                </thead>
                                <tbody>
                                  {(pickup.items || []).map((item, idx) => (
                                      <tr key={`${item.productName}-${item.quantity}-${idx}`} className="ppk-border-dashed">
                                      <td className="ppk-item-name">{item.productName}</td>
                                      <td className="text-right font-bold text-nowrap ppk-item-cell">{formatQuantity(item.quantity)}</td>
                                      <td className="text-right font-mono text-nowrap ppk-item-cell">{formatBsS(item.unitPriceBsS)}</td>
                                      <td className="text-right font-mono font-bold text-nowrap ppk-item-cell">{formatBsS(item.subtotalBsS)}</td>
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
                      <div className="font-bold text-base flex-align-center gap-1 cursor-pointer" onClick={() => toggleExpand(pickup.saleId)}>
                        {isExpanded ? <ChevronDown size={18} className="ppk-caret-mobile" /> : <ChevronRight size={18} className="ppk-caret-mobile" />}
                        Factura N° {pickup.invoiceNumber || pickup.saleId}
                      </div>
                      <div className="text-xs text-muted mt-1">
                        {new Date(pickup.date).toLocaleDateString()} {new Date(pickup.date).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                      </div>
                    </div>

                    <span className="badge badge-warning ppk-custody-badge ppk-custody-badge-md">
                      En Custodia
                    </span>
                  </div>

                  {/* Customer Info Box */}
                  <div className="pending-mobile-card-customer">
                    <div className="flex-align-center gap-1 font-bold pending-mobile-card-customer-name overflow-hidden ppk-customer-mobile">
                      <User size={15} className="text-muted flex-shrink-0" />
                      <span
                        title={pickup.customerName || 'Consumidor Final'}
                        className="text-truncate d-inline-block ppk-maxw100"
                      >
                        {pickup.customerName || 'Consumidor Final'}
                      </span>
                    </div>
                    <div className="text-xs text-muted mt-1 ml-4 d-flex flex-column ppk-customer-meta-col">
                      <span>RIF/Cédula: <strong>{pickup.customerCedula || 'N/A'}</strong></span>
                      {pickup.customerPhone && <span>Tel: <strong>{pickup.customerPhone}</strong></span>}
                    </div>
                  </div>

                  {/* Financial Breakdown Grid */}
                  <div
                    className="pending-mobile-card-summary ppk-summary-grid"
                  >
                    <div>
                      <div className="text-xs text-muted mb-1">Total (USD)</div>
                      <div className="font-bold text-primary text-nowrap ppk-total-sm">{formatUSD(pickup.totalUSD)}</div>
                    </div>
                    <div className="text-right">
                      <div className="text-xs text-muted mb-1">Total (Bs.S)</div>
                      <div className="font-bold font-mono text-primary text-nowrap ppk-total-sm">{formatBsS(pickup.totalBsS).replace(/^Bs\.S\s?/, '')}</div>
                    </div>
                  </div>

                  {/* Touch-friendly Action Buttons */}
                  <div className="pending-mobile-card-actions">
                    <button
                      type="button"
                      className="btn btn-outline flex-1 flex-align-center justify-center gap-1 ppk-confirm-btn-mobile"
                      onClick={() => handleConfirmPickupClick(pickup)}
                    >
                      <PackageCheck size={18} /> Confirmar Retiro
                    </button>

                    <button
                      type="button"
                      className="btn btn-outline flex-1 flex-align-center justify-center gap-1 w-full ppk-toggle-btn"
                      onClick={() => toggleExpand(pickup.saleId)}
                    >
                      {isExpanded ? <ChevronDown size={16} /> : <ChevronRight size={16} />} Detalle ({pickup.items?.length || 0} productos)
                    </button>
                  </div>

                  {/* Mobile Collapsible Detail */}
                  {isExpanded && (
                    <div className="ppk-border-top-dashed pt-3 mt-1 ppk-detail-mobile">
                      <h4 className="ppk-detail-h4-mobile">📦 Productos del Pedido</h4>
                      <div className="d-flex flex-column ppk-items-col">
                        {(pickup.items || []).map((item, idx) => (
                          <div key={`${item.productName}-${item.quantity}-${idx}`} className="flex-between align-start ppk-item-row ppk-border-bottom">
                            <div className="ppk-item-main">
                              <div><strong>{item.productName}</strong></div>
                              <div className="text-xs text-muted">{formatQuantity(item.quantity)} unds x {formatBsS(item.unitPriceBsS)}</div>
                            </div>
                            <div className="font-bold font-mono text-right text-nowrap flex-shrink-0">{formatBsS(item.subtotalBsS)}</div>
                          </div>
                        ))}
                      </div>
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
                className="btn btn-outline flex-align-center gap-2 mx-auto ppk-load-more"
                onClick={loadMore}
                disabled={loading}
              >
                {loading ? <Loader2 size={16} className="animate-spin" /> : <ChevronDown size={16} />}
                {loading ? 'Cargando...' : 'Ver más retiros'}
              </button>
            </div>
          )}
        </>
      )}

      {/* Modal de Confirmación de Retiro */}
      {selectedPickup && (
        <Modal
          isOpen={Boolean(selectedPickup)}
          onClose={() => setSelectedPickup(null)}
          title="Confirmar Entrega de Mercancía"
          maxWidth="480px"
        >
          <div className="p-2 text-center ppk-modal-body">
            <div className="ppk-confirm-icon">
              <PackageCheck size={28} />
            </div>

            <h4 className="font-bold mb-2 text-primary ppk-modal-title">
              ¿Entregar pedido a <span className="ppk-customer-white">{selectedPickup.customerName || 'Consumidor Final'}</span>?
            </h4>
            <p className="text-muted text-sm mb-4 ppk-modal-text">
              Se registrará la salida física de la mercancía correspondiente a la <span className="font-bold ppk-modal-highlight">Factura N° {selectedPickup.invoiceNumber || selectedPickup.saleId}</span>.
            </p>

            <div className="d-flex justify-center gap-3 ppk-modal-actions ppk-border-top-solid">
              <button
                type="button"
                className="btn btn-outline ppk-btn-min110"
                onClick={() => setSelectedPickup(null)}
                disabled={isConfirming}
              >
                Cancelar
              </button>
              <button
                type="button"
                className="btn btn-primary d-inline-flex flex-align-center justify-center gap-2 ppk-btn-confirm"
                onClick={handleExecutePickup}
                disabled={isConfirming}
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
