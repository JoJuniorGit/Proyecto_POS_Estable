import React, { useState, useEffect, useCallback } from 'react';
import { getSalesHistory, getSaleHistoryDetail } from '../services/historyApi';
import { Search, Loader2, Calendar, ChevronRight, ChevronLeft, ChevronDown, RefreshCw, CheckCircle, Clock, XCircle, FileText } from 'lucide-react';
import { useExchangeRate } from '../context/ExchangeRateContext';
import { formatBsS, formatUSD, formatNumberEs } from '../utils/formatters';

const PAGE_SIZE = 25;

export default function HistoryPage() {
  const { exchangeRate } = useExchangeRate();
  const [sales, setSales] = useState([]);
  const [loading, setLoading] = useState(false);
  const [startDate, setStartDate] = useState('');
  const [endDate, setEndDate] = useState('');
  const [currentPage, setCurrentPage] = useState(1);
  const [totalCount, setTotalCount] = useState(0);

  const [expandedSaleId, setExpandedSaleId] = useState(null);
  const [saleDetails, setSaleDetails] = useState({});
  const [error, setError] = useState(null);

  // Requisito: Paginación de 25 pedidos por página
  const fetchHistory = useCallback(async (pageOverride) => {
    const pageToFetch = pageOverride !== undefined ? pageOverride : currentPage;
    setLoading(true);
    setError(null);
    try {
      const data = await getSalesHistory(pageToFetch, PAGE_SIZE, startDate, endDate);
      const items = data?.items || data?.Items || (Array.isArray(data) ? data : []);
      const total = data?.totalCount ?? data?.TotalCount ?? items.length;

      setSales(items);
      setTotalCount(total);
    } catch (err) {
      console.error('[HistoryPage] Error al cargar historial:', err);
      setError('No se pudo cargar el historial de ventas.');
    } finally {
      setLoading(false);
    }
  }, [currentPage, startDate, endDate]);

  useEffect(() => {
    fetchHistory();
  }, [fetchHistory]);

  const handleSearchClick = () => {
    setCurrentPage(1);
    fetchHistory(1);
  };

  const handlePageChange = (newPage) => {
    const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));
    if (newPage < 1 || newPage > totalPages || newPage === currentPage) return;
    setCurrentPage(newPage);
    fetchHistory(newPage);
  };

  const toggleExpand = async (id) => {
    if (expandedSaleId === id) {
      setExpandedSaleId(null);
      return;
    }
    
    setExpandedSaleId(id);

    if (!saleDetails[id]) {
      setSaleDetails(prev => ({ ...prev, [id]: { loading: true, data: null, error: null } }));
      try {
        const detail = await getSaleHistoryDetail(id);
        setSaleDetails(prev => ({ ...prev, [id]: { loading: false, data: detail, error: null } }));
      } catch (err) {
        setSaleDetails(prev => ({ ...prev, [id]: { loading: false, data: null, error: 'No se pudieron cargar los detalles.' } }));
      }
    }
  };

  const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));

  return (
    <div className="history-page">
      {/* ── Encabezado Principal ── */}
      <div className="history-header flex-between mb-4">
        <h2 className="history-header-title page-title flex-align-center gap-2">
          <FileText size={24} className="color-primary flex-shrink-0" />
          <span>Historial de Ventas</span>
        </h2>
        <div className="history-header-actions flex-align-center gap-2">
          <button
            type="button"
            className="btn btn-outline btn-sm flex-align-center gap-2"
            onClick={() => fetchHistory(currentPage)}
            disabled={loading}
            style={{ display: 'inline-flex', alignItems: 'center', gap: '6px' }}
          >
            <RefreshCw size={16} className={loading ? 'animate-spin' : ''} /> Actualizar
          </button>
        </div>
      </div>

      {/* ── Filtros de Fecha ── */}
      <div className="card mb-4 p-3" style={{ marginBottom: '20px' }}>
        <div className="history-filter-row">
          <div className="form-group mb-0 history-filter-item">
            <label className="form-label text-xs font-medium text-muted">Fecha Inicio</label>
            <input
              type="date"
              className="form-control"
              value={startDate}
              onChange={(e) => setStartDate(e.target.value)}
              style={{ width: '100%' }}
            />
          </div>

          <div className="form-group mb-0 history-filter-item">
            <label className="form-label text-xs font-medium text-muted">Fecha Fin</label>
            <input
              type="date"
              className="form-input"
              value={endDate}
              onChange={(e) => setEndDate(e.target.value)}
              style={{ width: '100%' }}
            />
          </div>

          <div className="form-group mb-0 history-filter-item history-filter-btn-group">
            <button type="button" className="btn btn-primary history-search-btn" onClick={handleSearchClick}>
              <Search size={16} /> Buscar
            </button>
          </div>
        </div>
      </div>

      {error && <div className="alert alert-danger mb-4">{error}</div>}

      {/* ── Contenido del Historial (Escritorio vs Móvil) ── */}
      <div className="card padding-none overflow-hidden" style={{ borderRadius: '12px', border: '1px solid var(--border-color)' }}>
        {loading ? (
          <div className="flex-center p-5" style={{ padding: '60px', textAlign: 'center' }}>
            <Loader2 className="animate-spin mb-2 mx-auto" size={28} />
            <div>Cargando historial de ventas...</div>
          </div>
        ) : sales.length === 0 ? (
          <div className="text-center p-5 text-muted" style={{ padding: '60px' }}>
            <Calendar size={48} className="mx-auto mb-2 opacity-50" />
            <p>No se encontraron registros de ventas.</p>
          </div>
        ) : (
          <>
            {/* ── 3A. VISTA MÓVIL (TARJETAS FLUIDAS EN 3 PISOS CON ETIQUETAS CONTEXTUALES) ── */}
            <div className="history-mobile-cards-view p-3">
              {sales.map((sale) => {
                const isExpanded = expandedSaleId === sale.id;
                const dateOnlyStr = sale.date ? new Date(sale.date).toLocaleDateString('es-VE') : '-';
                const totalBsS = sale.totalBsS > 0
                  ? sale.totalBsS
                  : (sale.totalUSD || 0) * (sale.appliedRate || exchangeRate);
                const detailState = saleDetails[sale.id];
                const customerName = sale.customerName || 'Consumidor Final';
                const cashierName = sale.cashierName || 'Usuario Desconocido';
                const invoiceNum = sale.invoiceNumber ? sale.invoiceNumber.toString().padStart(6, '0') : sale.id;

                const rawStatus = (sale.status ?? '').toString().trim().toLowerCase();
                const isCancelled = rawStatus === 'cancelled' || rawStatus === 'anulada' || rawStatus === '2';
                const isPending = rawStatus === 'pending' || rawStatus === '0';
                const isOnHold = rawStatus === 'onhold' || rawStatus === '3';
                const isCompleted = rawStatus === 'completed' || rawStatus === 'pagado' || rawStatus === '1' || (!isCancelled && !isPending && !isOnHold);

                return (
                  <div key={sale.id} className="history-mobile-card p-3 mb-3 border rounded-lg bg-surface shadow-xs">
                    
                    {/* Piso Superior: Identificación y Estado */}
                    <div className="flex-between flex-align-center mb-2.5 pb-2 border-bottom cursor-pointer" onClick={() => toggleExpand(sale.id)}>
                      <div className="font-bold text-base flex-align-center gap-1.5 color-primary">
                        {isExpanded ? <ChevronDown size={18} /> : <ChevronRight size={18} />}
                        <span>N° {invoiceNum}</span>
                      </div>

                      <div>
                        {isCompleted ? (
                          <span className="history-status-badge badge-success-subtle">
                            <CheckCircle size={13} /> Completada
                          </span>
                        ) : isCancelled ? (
                          <span className="history-status-badge badge-danger-subtle">
                            <XCircle size={13} /> Anulada
                          </span>
                        ) : (
                          <span className="history-status-badge badge-warning-subtle">
                            <Clock size={13} /> {isOnHold ? 'En Espera' : 'Pendiente'}
                          </span>
                        )}
                      </div>
                    </div>

                    {/* Piso Medio: Datos Operativos con Etiquetas Contextuales */}
                    <div className="history-mobile-card-body text-xs mb-2.5 pb-2 border-bottom flex flex-column gap-1.5">
                      {/* Cliente + Cédula en su propia línea */}
                      <div>
                        <div className="flex-align-center gap-1">
                          <span className="text-muted text-xs">Cliente:</span>
                          <span 
                            className="font-bold text-sm" 
                            title={customerName}
                            style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: '200px', display: 'inline-block', verticalAlign: 'bottom' }}
                          >
                            {customerName}
                          </span>
                        </div>
                        <div className="text-muted text-xs ml-4 font-mono">
                          {sale.customerCedula || 'V-00000000'}
                        </div>
                      </div>

                      {/* Cajero */}
                      <div className="flex-align-center gap-1">
                        <span className="text-muted text-xs">Cajero:</span>
                        <span className="font-medium">{cashierName}</span>
                      </div>

                      {/* Fecha (Sólo fecha plana sin hora) */}
                      <div className="flex-align-center gap-1">
                        <span className="text-muted text-xs">Fecha:</span>
                        <span className="font-mono">{dateOnlyStr}</span>
                      </div>
                    </div>

                    {/* Piso Inferior: Finanzas Resaltadas */}
                    <div className="flex-between flex-align-center text-xs pt-1">
                      <div>
                        <span className="text-muted block text-2xs mb-0.5">Total USD</span>
                        <span className="font-mono font-bold text-sm">{formatUSD(sale.totalUSD || 0)}</span>
                      </div>

                      <div className="text-right">
                        <span className="text-muted block text-2xs mb-0.5">Total Bs.S</span>
                        <span className="font-mono font-bold text-base color-primary">{formatBsS(totalBsS)}</span>
                      </div>
                    </div>

                    {/* Fila Desplegable de Detalle en Móvil */}
                    {isExpanded && (
                      <div className="mt-3 pt-3 border-top text-xs">
                        {detailState?.loading ? (
                          <div className="flex-center p-3 text-muted">
                            <Loader2 className="animate-spin mr-2" size={18} /> Cargando detalles...
                          </div>
                        ) : detailState?.error ? (
                          <div className="alert alert-danger text-xs p-2">{detailState.error}</div>
                        ) : detailState?.data ? (
                          <div className="flex flex-column gap-3">
                            
                            <div className="p-2.5 rounded bg-tertiary">
                              <div className="font-bold text-xs mb-1">
                                Factura N° {detailState.data.invoiceNumber || sale.id}
                              </div>
                              <div className="text-muted text-2xs font-mono">
                                Hora de Emisión: <strong>{new Date(detailState.data.date || sale.date).toLocaleTimeString('es-VE', { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: true })}</strong>
                              </div>
                            </div>

                            <div>
                              <h4 className="font-bold text-xs mb-1.5">📦 Artículos Vendidos ({detailState.data.items?.length || 0})</h4>
                              <div className="border rounded overflow-hidden">
                                <table className="cart-table text-2xs" style={{ width: '100%' }}>
                                  <thead>
                                    <tr>
                                      <th style={{ padding: '6px' }}>Producto</th>
                                      <th className="text-center" style={{ padding: '6px' }}>Cant.</th>
                                      <th className="text-right" style={{ padding: '6px' }}>Subtotal Bs.S</th>
                                    </tr>
                                  </thead>
                                  <tbody>
                                    {(detailState.data.items || []).map((item) => (
                                      <tr key={item.id}>
                                        <td className="font-medium" style={{ padding: '6px' }}>{item.productName}</td>
                                        <td className="text-center font-mono" style={{ padding: '6px' }}>{item.quantity}</td>
                                        <td className="text-right font-mono font-bold color-primary" style={{ padding: '6px' }}>{formatBsS(item.subtotalBsS)}</td>
                                      </tr>
                                    ))}
                                  </tbody>
                                </table>
                              </div>
                            </div>

                            {detailState.data.payments?.length > 0 && (
                              <div>
                                <h4 className="font-bold text-xs mb-1.5">💳 Métodos de Pago</h4>
                                <div className="flex flex-column gap-1">
                                  {detailState.data.payments.map((pay, idx) => (
                                    <div key={idx} className="flex-between p-1.5 border rounded bg-surface text-2xs">
                                      <span>{pay.methodName} {pay.reference ? `(Ref: ${pay.reference})` : ''}</span>
                                      <span className="font-bold font-mono">{formatBsS(pay.amountBsS || 0)}</span>
                                    </div>
                                  ))}
                                </div>
                              </div>
                            )}

                          </div>
                        ) : null}
                      </div>
                    )}

                  </div>
                );
              })}
            </div>

            {/* ── 3B. VISTA ESCRITORIO (TABLA TRADICIONAL) ── */}
            <div className="history-desktop-table-view history-table-wrapper">
              <table className="cart-table history-main-table">
                <thead>
                  <tr>
                    <th style={{ width: '40px', textAlign: 'center' }}></th>
                    <th style={{ whiteSpace: 'nowrap' }}>N° Factura</th>
                    <th style={{ whiteSpace: 'nowrap' }}>Fecha</th>
                    <th style={{ whiteSpace: 'nowrap' }}>Cliente</th>
                    <th style={{ whiteSpace: 'nowrap' }}>Cajero</th>
                    <th style={{ textAlign: 'right', whiteSpace: 'nowrap', paddingRight: '16px' }}>Total USD</th>
                    <th style={{ textAlign: 'right', whiteSpace: 'nowrap', paddingRight: '16px' }}>Total Bs.S</th>
                    <th style={{ textAlign: 'center', whiteSpace: 'nowrap' }}>Estado</th>
                  </tr>
                </thead>
                <tbody>
                  {sales.map((sale) => {
                    const isExpanded = expandedSaleId === sale.id;
                    
                    const dateStr = sale.date ? new Date(sale.date).toLocaleDateString('es-VE') : '-';

                    const totalBsS = sale.totalBsS > 0
                      ? sale.totalBsS
                      : (sale.totalUSD || 0) * (sale.appliedRate || exchangeRate);
                    
                    const detailState = saleDetails[sale.id];
                    const customerName = sale.customerName || 'Consumidor Final';

                    const rawStatus = (sale.status ?? '').toString().trim().toLowerCase();
                    const isCancelled = rawStatus === 'cancelled' || rawStatus === 'anulada' || rawStatus === '2';
                    const isPending = rawStatus === 'pending' || rawStatus === '0';
                    const isOnHold = rawStatus === 'onhold' || rawStatus === '3';
                    const isCompleted = rawStatus === 'completed' || rawStatus === 'pagado' || rawStatus === '1' || (!isCancelled && !isPending && !isOnHold);

                    return (
                      <React.Fragment key={sale.id}>
                        <tr className="cursor-pointer" onClick={() => toggleExpand(sale.id)}>
                          <td className="text-center">
                            {isExpanded ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
                          </td>
                          <td className="font-bold font-mono" style={{ whiteSpace: 'nowrap' }}>
                            N° {sale.invoiceNumber ? sale.invoiceNumber.toString().padStart(6, '0') : sale.id}
                          </td>
                          
                          <td style={{ whiteSpace: 'nowrap' }} className="font-mono text-sm">
                            {dateStr}
                          </td>

                          <td style={{ maxWidth: '180px', whiteSpace: 'nowrap' }}>
                            <div 
                              className="font-medium" 
                              title={customerName}
                              style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: '180px' }}
                            >
                              {customerName}
                            </div>
                            <div style={{ fontSize: '0.75rem', color: 'var(--text-muted)' }}>
                              {sale.customerCedula || 'V-00000000'}
                            </div>
                          </td>

                          <td style={{ whiteSpace: 'nowrap' }}>
                            <span className="font-medium">{sale.cashierName || 'Usuario Desconocido'}</span>
                          </td>

                          <td className="text-right font-mono font-medium" style={{ whiteSpace: 'nowrap', paddingRight: '16px' }}>
                            {formatUSD(sale.totalUSD || 0)}
                          </td>
                          <td className="text-right font-mono font-bold color-primary" style={{ whiteSpace: 'nowrap', paddingRight: '16px' }}>
                            {formatBsS(totalBsS)}
                          </td>

                          {/* Etiqueta Visual de Estado */}
                          <td className="text-center" style={{ whiteSpace: 'nowrap' }}>
                            {isCompleted ? (
                              <span className="history-status-badge badge-success-subtle">
                                <CheckCircle size={13} /> Completada
                              </span>
                            ) : isCancelled ? (
                              <span className="history-status-badge badge-danger-subtle">
                                <XCircle size={13} /> Anulada
                              </span>
                            ) : (
                              <span className="history-status-badge badge-warning-subtle">
                                <Clock size={13} /> {isOnHold ? 'En Espera' : 'Pendiente'}
                              </span>
                            )}
                          </td>
                        </tr>

                        {/* Fila Desplegable de Detalle Escritorio */}
                        {isExpanded && (
                          <tr className="history-detail-row">
                            <td colSpan={8} style={{ padding: '16px', backgroundColor: 'var(--bg-tertiary, rgba(128,128,128,0.05))' }}>
                              {detailState?.loading ? (
                                <div className="flex-center p-3 text-muted">
                                  <Loader2 className="animate-spin mr-2" size={18} /> Cargando detalles de la factura...
                                </div>
                              ) : detailState?.error ? (
                                <div className="alert alert-danger text-sm">{detailState.error}</div>
                              ) : detailState?.data ? (
                                <div className="grid grid-1 sm:grid-2 gap-4">
                                  
                                  <div>
                                    <h4 className="font-bold mb-2 text-sm sm:text-base">Artículos Vendidos ({detailState.data.items?.length || 0})</h4>
                                    <div className="history-detail-table-wrapper border rounded overflow-x-auto">
                                      <table className="cart-table text-xs" style={{ width: '100%' }}>
                                        <thead>
                                          <tr>
                                            <th>Producto</th>
                                            <th className="text-center">Cant.</th>
                                            <th className="text-right">P. Unit USD</th>
                                            <th className="text-right">Subtotal Bs.S</th>
                                          </tr>
                                        </thead>
                                        <tbody>
                                          {(detailState.data.items || []).map((item) => (
                                            <tr key={item.id}>
                                              <td className="font-medium">{item.productName}</td>
                                              <td className="text-center font-mono">{item.quantity}</td>
                                              <td className="text-right font-mono">{formatUSD(item.unitPrice)}</td>
                                              <td className="text-right font-mono font-bold color-primary">{formatBsS(item.subtotalBsS)}</td>
                                            </tr>
                                          ))}
                                        </tbody>
                                      </table>
                                    </div>
                                  </div>

                                  <div>
                                    <h4 className="font-bold mb-2 text-sm sm:text-base">Resumen Financiero</h4>
                                    <div className="p-3 border rounded mb-3 text-xs sm:text-sm" style={{ backgroundColor: 'var(--bg-surface)' }}>
                                      <div className="flex-between mb-1">
                                        <span className="text-muted">Cliente:</span>
                                        <span className="font-bold">{detailState.data.customerName || 'Consumidor Final'}</span>
                                      </div>
                                      <div className="flex-between mb-1">
                                        <span className="text-muted">Cédula / RIF:</span>
                                        <span className="font-bold">{detailState.data.customerCedula || 'V-00000000'}</span>
                                      </div>
                                      <div className="flex-between mb-1">
                                        <span className="text-muted">Tasa de Cambio:</span>
                                        <span className="font-bold">Bs.S {formatNumberEs(detailState.data.appliedRate)}</span>
                                      </div>
                                      <div className="flex-between mb-1">
                                        <span className="text-muted">Total USD:</span>
                                        <span className="font-bold">{formatUSD(detailState.data.totalUSD || 0)}</span>
                                      </div>
                                      <div className="flex-between">
                                        <span className="text-muted">Total Bs.S:</span>
                                        <span className="font-bold color-primary">{formatBsS(detailState.data.totalBsS || 0)}</span>
                                      </div>
                                    </div>

                                    <h4 className="font-bold mb-2 text-sm sm:text-base">Métodos de Pago</h4>
                                    {detailState.data.payments?.length > 0 ? (
                                      <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
                                        {detailState.data.payments.map((pay, idx) => (
                                          <div key={idx} className="history-payment-item">
                                            <div>
                                              <div className="font-medium">{pay.methodName}</div>
                                              {pay.reference && <div className="text-xs text-muted">Ref: {pay.reference}</div>}
                                            </div>
                                            <div className="font-bold">
                                              {formatBsS(pay.amountBsS || 0)}
                                            </div>
                                          </div>
                                        ))}
                                      </div>
                                    ) : (
                                      <div className="text-muted text-sm">No hay registros de pago.</div>
                                    )}
                                  </div>
                                  
                                </div>
                              ) : null}
                            </td>
                          </tr>
                        )}
                      </React.Fragment>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </>
        )}
      </div>

      {/* ── Controles de Paginación (25 Pedidos por Página) ── */}
      {totalCount > 0 && (
        <div className="history-pagination-container card mt-3 p-3 flex-between flex-align-center flex-wrap gap-3" style={{ marginTop: '16px', borderRadius: '12px' }}>
          <div className="text-xs sm:text-sm text-muted font-medium">
            Mostrando <span className="font-bold color-primary">{((currentPage - 1) * PAGE_SIZE) + 1}</span> a{' '}
            <span className="font-bold color-primary">{Math.min(currentPage * PAGE_SIZE, totalCount)}</span> de{' '}
            <span className="font-bold color-primary">{totalCount}</span> pedidos
          </div>

          <div className="flex-align-center gap-1.5 history-pagination-buttons">
            <button
              type="button"
              className="btn btn-outline btn-sm flex-align-center gap-1"
              disabled={currentPage === 1 || loading}
              onClick={() => handlePageChange(currentPage - 1)}
              style={{ padding: '6px 12px', fontSize: '0.825rem' }}
            >
              <ChevronLeft size={16} /> Anterior
            </button>

            <div className="flex-align-center gap-1 mx-1">
              {Array.from({ length: totalPages }, (_, i) => i + 1)
                .filter(p => p === 1 || p === totalPages || Math.abs(p - currentPage) <= 1)
                .map((p, idx, arr) => {
                  const prev = arr[idx - 1];
                  const showEllipsis = prev && p - prev > 1;
                  return (
                    <React.Fragment key={p}>
                      {showEllipsis && <span className="text-muted px-1" style={{ fontSize: '0.8rem' }}>...</span>}
                      <button
                        type="button"
                        className={`btn btn-sm ${p === currentPage ? 'btn-primary' : 'btn-outline'}`}
                        style={{ minWidth: '34px', height: '32px', padding: '2px 8px', fontWeight: p === currentPage ? 700 : 500 }}
                        onClick={() => handlePageChange(p)}
                        disabled={loading}
                      >
                        {p}
                      </button>
                    </React.Fragment>
                  );
                })}
            </div>

            <button
              type="button"
              className="btn btn-outline btn-sm flex-align-center gap-1"
              disabled={currentPage >= totalPages || loading}
              onClick={() => handlePageChange(currentPage + 1)}
              style={{ padding: '6px 12px', fontSize: '0.825rem' }}
            >
              Siguiente <ChevronRight size={16} />
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
