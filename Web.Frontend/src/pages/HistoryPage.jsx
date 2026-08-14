import React, { useState, useEffect, useCallback } from 'react';
import { getSalesHistory, getSaleHistoryDetail } from '../services/historyApi';
import { Search, Loader2, Calendar, ChevronRight, ChevronLeft, ChevronDown, RefreshCw, CheckCircle, Clock, XCircle, FileText } from 'lucide-react';
import { useExchangeRate } from '../context/ExchangeRateContext';
import { formatBsS, formatUSD, formatNumberEs, formatDate, formatTime } from '../utils/formatters';

const PAGE_SIZE = 25;

// Tokens de estilo para jerarquía tipográfica (etiquetas vs valores)
const labelStyle = { color: '#a0aec0', fontWeight: 400 };
const valueStyle = { color: 'var(--text-primary)', fontWeight: 600 };
const tableHeaderStyle = {
  color: 'var(--text-secondary, #94a3b8)',
  fontWeight: 600,
  whiteSpace: 'nowrap',
};

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
            {/* ── 3A. VISTA MÓVIL (TARJETAS FLUIDAS) ── */}
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
                  <div
                    key={sale.id}
                    className="history-mobile-card p-3 mb-3 bg-surface"
                    style={{
                      padding: '14px',
                      marginBottom: '14px',
                      backgroundColor: 'var(--bg-surface)',
                      border: '1px solid var(--border-color)',
                      borderRadius: '10px',
                      boxShadow: '0 2px 8px rgba(0, 0, 0, 0.05)',
                      display: 'flex',
                      flexDirection: 'column',
                      gap: '10px'
                    }}
                  >

                    {/* Piso Superior: Identificación y Estado */}
                    <div
                      onClick={() => toggleExpand(sale.id)}
                      style={{
                        display: 'flex',
                        justifyContent: 'space-between',
                        alignItems: 'center',
                        paddingBottom: '8px',
                        borderBottom: '1px solid var(--border-color)',
                        cursor: 'pointer'
                      }}
                    >
                      <div style={{ display: 'flex', alignItems: 'center', gap: '6px', fontWeight: 700, fontSize: '0.95rem' }}>
                        {isExpanded ? <ChevronDown size={18} /> : <ChevronRight size={18} />}
                        <span style={{ color: 'var(--text-primary)' }}>N° {invoiceNum}</span>
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
                    <div
                      style={{
                        display: 'flex',
                        flexDirection: 'column',
                        gap: '6px',
                        paddingBottom: '8px',
                        borderBottom: '1px solid var(--border-color)',
                        fontSize: '0.8rem'
                      }}
                    >
                      {/* Cliente + Cédula en su propia línea */}
                      <div>
                        <div style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
                          <span style={labelStyle}>Cliente:</span>
                          <span
                            style={{ ...valueStyle, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: '200px', display: 'inline-block', verticalAlign: 'bottom' }}
                            title={customerName}
                          >
                            {customerName}
                          </span>
                        </div>
                        <div style={{ ...labelStyle, fontFamily: 'monospace', marginLeft: '16px' }}>
                          {sale.customerCedula || 'V-00000000'}
                        </div>
                      </div>

                      {/* Cajero */}
                      <div style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
                        <span style={labelStyle}>Cajero:</span>
                        <span style={valueStyle}>{cashierName}</span>
                      </div>

                      {/* Fecha (Sólo fecha plana sin hora) */}
                      <div style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
                        <span style={labelStyle}>Fecha:</span>
                        <span style={{ ...valueStyle, fontFamily: 'monospace' }}>{dateOnlyStr}</span>
                      </div>
                    </div>

                    {/* Piso Inferior: Finanzas Resaltadas */}
                    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', width: '100%', fontSize: '0.8rem', paddingTop: '2px' }}>
                      <div>
                        <span style={{ ...labelStyle, display: 'block', fontSize: '0.7rem', marginBottom: '2px' }}>Total USD:</span>
                        <span style={{ ...valueStyle, fontFamily: 'monospace', fontSize: '0.875rem' }}>{formatUSD(sale.totalUSD || 0)}</span>
                      </div>

                      <div style={{ textAlign: 'right' }}>
                        <span style={{ ...labelStyle, display: 'block', fontSize: '0.7rem', marginBottom: '2px' }}>Total Bs.S:</span>
                        <span style={{ ...valueStyle, fontFamily: 'monospace', fontSize: '0.95rem' }}>{formatNumberEs(totalBsS)}</span>
                      </div>
                    </div>

                    {/* Fila Desplegable de Detalle en Móvil */}
                    {isExpanded && (
                      <div style={{ borderTop: '1px dashed var(--border-color)', paddingTop: '12px', fontSize: '0.8rem' }}>
                        {detailState?.loading ? (
                          <div className="flex-center p-3 text-muted">
                            <Loader2 className="animate-spin mr-2" size={18} /> Cargando detalles...
                          </div>
                        ) : detailState?.error ? (
                          <div className="alert alert-danger text-xs p-2">{detailState.error}</div>
                        ) : detailState?.data ? (
                          <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>

                            <div style={{ padding: '10px', borderRadius: '8px', backgroundColor: 'var(--bg-tertiary, rgba(128,128,128,0.08))' }}>
                              <div style={{ fontWeight: 700, fontSize: '0.75rem', marginBottom: '4px' }}>
                                Factura N° {detailState.data.invoiceNumber || sale.id}
                              </div>
                              <div style={{ ...labelStyle, fontSize: '0.7rem', fontFamily: 'monospace' }}>
                                Hora de Emisión: <strong style={{ color: 'var(--text-primary)', fontWeight: 600 }}>{new Date(detailState.data.date || sale.date).toLocaleTimeString('es-VE', { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: true })}</strong>
                              </div>
                            </div>

                            <div>
                              <h4 style={{ fontWeight: 700, fontSize: '0.75rem', marginBottom: '6px' }}>📦 Artículos Vendidos ({detailState.data.items?.length || 0})</h4>
                              <div style={{ border: '1px solid var(--border-color)', borderRadius: '8px', overflow: 'hidden' }}>
                                <table className="cart-table" style={{ width: '100%', fontSize: '0.7rem' }}>
                                  <thead>
                                    <tr>
                                      <th style={{ padding: '6px', color: 'var(--text-secondary, #94a3b8)' }}>Producto</th>
                                      <th style={{ padding: '6px', textAlign: 'right', color: 'var(--text-secondary, #94a3b8)' }}>Cant.</th>
                                      <th style={{ padding: '6px', textAlign: 'right', color: 'var(--text-secondary, #94a3b8)' }}>Subtotal Bs.S</th>
                                    </tr>
                                  </thead>
                                  <tbody>
                                    {(detailState.data.items || []).map((item) => (
                                      <tr key={item.id}>
                                        <td className="font-medium" style={{ padding: '6px' }}>{item.productName}</td>
                                        <td style={{ padding: '6px', textAlign: 'right', fontFamily: 'monospace' }}>{item.quantity}</td>
                                        <td style={{ padding: '6px', textAlign: 'right', fontFamily: 'monospace', fontWeight: 700 }}>{formatBsS(item.subtotalBsS)}</td>
                                      </tr>
                                    ))}
                                  </tbody>
                                </table>
                              </div>
                            </div>

                            {detailState.data.payments?.length > 0 && (
                              <div>
                                <h4 style={{ fontWeight: 700, fontSize: '0.75rem', marginBottom: '6px' }}>💳 Métodos de Pago</h4>
                                <div style={{ display: 'flex', flexDirection: 'column', gap: '4px' }}>
                                  {detailState.data.payments.map((pay, idx) => (
                                    <div
                                      key={idx}
                                      style={{
                                        display: 'flex',
                                        justifyContent: 'space-between',
                                        alignItems: 'center',
                                        padding: '6px 8px',
                                        border: '1px solid var(--border-color)',
                                        borderRadius: '8px',
                                        backgroundColor: 'var(--bg-surface)',
                                        fontSize: '0.7rem'
                                      }}
                                    >
                                      <span>{pay.methodName} {pay.reference ? `(Ref: ${pay.reference})` : ''}</span>
                                      <span style={{ fontWeight: 700, fontFamily: 'monospace' }}>{formatBsS(pay.amountBsS || 0)}</span>
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
                    <th style={{ ...tableHeaderStyle, textAlign: 'left' }}>N° Factura</th>
                    <th style={tableHeaderStyle}>Cliente</th>
                    <th style={tableHeaderStyle}>Cajero</th>
                    <th style={{ ...tableHeaderStyle, textAlign: 'right', paddingRight: '16px' }}>Total Bs.S</th>
                    <th style={{ ...tableHeaderStyle, textAlign: 'center' }}>Estado</th>
                  </tr>
                </thead>
                <tbody>
                  {sales.map((sale) => {
                    const isExpanded = expandedSaleId === sale.id;

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
                          {/* Ícono + N° de factura como una unidad */}
                          <td style={{ whiteSpace: 'nowrap' }}>
                            <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                              {isExpanded ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
                              <span className="font-bold font-mono" style={{ color: 'var(--text-primary)' }}>
                                N° {sale.invoiceNumber ? sale.invoiceNumber.toString().padStart(6, '0') : sale.id}
                              </span>
                            </div>
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

                          <td style={{ textAlign: 'right', whiteSpace: 'nowrap', paddingRight: '16px' }}>
                            <div style={{ fontFamily: 'monospace', fontWeight: 700, color: 'var(--text-primary)' }}>{formatBsS(totalBsS)}</div>
                            <div style={{ fontSize: '0.75rem', color: 'var(--text-muted)', fontWeight: 400 }}>
                              {formatUSD(sale.totalUSD || 0)}
                            </div>
                          </td>

                          {/* Etiqueta Visual de Estado */}
                          <td style={{ textAlign: 'center', whiteSpace: 'nowrap' }}>
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
                            <td colSpan={5} style={{ padding: '16px', backgroundColor: 'var(--bg-tertiary, rgba(128,128,128,0.05))' }}>
                              {detailState?.loading ? (
                                <div className="flex-center p-3 text-muted">
                                  <Loader2 className="animate-spin mr-2" size={18} /> Cargando detalles de la factura...
                                </div>
                              ) : detailState?.error ? (
                                <div className="alert alert-danger text-sm">{detailState.error}</div>
                              ) : detailState?.data ? (
                                <>
                                  <div style={{ display: 'flex', alignItems: 'center', gap: '6px', marginBottom: '14px', fontSize: '0.875rem' }}>
                                    <Calendar size={16} style={{ flexShrink: 0 }} />
                                    <span style={{ fontWeight: 600 }}>
                                      {formatDate(detailState.data.date)} — {formatTime(detailState.data.date)}
                                    </span>
                                  </div>
                                  <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))', gap: '16px' }}>

                                  <div>
                                    <h4 className="font-bold mb-2" style={{ fontSize: '0.95rem' }}>Artículos Vendidos ({detailState.data.items?.length || 0})</h4>
                                    <div style={{ border: '1px solid var(--border-color)', borderRadius: '8px', overflowX: 'auto' }}>
                                      <table className="cart-table" style={{ width: '100%', fontSize: '0.8rem' }}>
                                        <thead>
                                          <tr>
                                            <th style={{ ...tableHeaderStyle, textAlign: 'left' }}>Producto</th>
                                            <th style={{ ...tableHeaderStyle, textAlign: 'right' }}>Cant.</th>
                                            <th style={{ ...tableHeaderStyle, textAlign: 'right' }}>P. Unidad</th>
                                            <th style={{ ...tableHeaderStyle, textAlign: 'right' }}>Subtotal Bs.S</th>
                                          </tr>
                                        </thead>
                                        <tbody>
                                          {(detailState.data.items || []).map((item) => (
                                            <tr key={item.id}>
                                              <td className="font-medium">{item.productName}</td>
                                              <td style={{ textAlign: 'right', fontFamily: 'monospace' }}>{item.quantity}</td>
                                              <td style={{ textAlign: 'right', fontFamily: 'monospace' }}>{formatBsS(item.unitPriceBsS)}</td>
                                              <td style={{ textAlign: 'right', fontFamily: 'monospace', fontWeight: 700 }}>{formatBsS(item.subtotalBsS)}</td>
                                            </tr>
                                          ))}
                                        </tbody>
                                      </table>
                                    </div>
                                  </div>

                                  <div>
                                    <h4 className="font-bold mb-2" style={{ fontSize: '0.95rem' }}>Resumen Financiero</h4>
                                    <div style={{ padding: '12px', border: '1px solid var(--border-color)', borderRadius: '8px', marginBottom: '12px', backgroundColor: 'var(--bg-surface)', fontSize: '0.875rem' }}>
                                      <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '4px' }}>
                                        <span className="text-muted">Cliente:</span>
                                        <span className="font-bold">{detailState.data.customerName || 'Consumidor Final'}</span>
                                      </div>
                                      <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '4px' }}>
                                        <span className="text-muted">Cédula / RIF:</span>
                                        <span className="font-bold">{detailState.data.customerCedula || 'V-00000000'}</span>
                                      </div>
                                      <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '4px' }}>
                                        <span className="text-muted">Tasa de Cambio:</span>
                                        <span className="font-bold">Bs.S {formatNumberEs(detailState.data.appliedRate)}</span>
                                      </div>
                                      <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '4px' }}>
                                        <span className="text-muted">Total USD:</span>
                                        <span className="font-bold">{formatUSD(detailState.data.totalUSD || 0)}</span>
                                      </div>
                                      <div style={{ display: 'flex', justifyContent: 'space-between' }}>
                                        <span className="text-muted">Total Bs.S:</span>
                                        <span className="font-bold" style={{ color: 'var(--text-primary)' }}>{formatBsS(detailState.data.totalBsS || 0)}</span>
                                      </div>
                                    </div>

                                    <h4 className="font-bold mb-2" style={{ fontSize: '0.95rem' }}>Métodos de Pago</h4>
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
                                </>
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
        <div
          className="card mt-3 p-3"
          style={{
            marginTop: '16px',
            borderRadius: '12px',
            display: 'flex',
            flexWrap: 'wrap',
            alignItems: 'center',
            justifyContent: 'space-between',
            gap: '12px'
          }}
        >
          <div style={{ fontSize: '0.875rem', color: 'var(--text-muted)', fontWeight: 500 }}>
            Mostrando <span style={{ fontWeight: 700, color: 'var(--accent-primary)' }}>{((currentPage - 1) * PAGE_SIZE) + 1}</span> a{' '}
            <span style={{ fontWeight: 700, color: 'var(--accent-primary)' }}>{Math.min(currentPage * PAGE_SIZE, totalCount)}</span> de{' '}
            <span style={{ fontWeight: 700, color: 'var(--accent-primary)' }}>{totalCount}</span> pedidos
          </div>

          <div style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
            <button
              type="button"
              className="btn btn-outline btn-sm"
              disabled={currentPage === 1 || loading}
              onClick={() => handlePageChange(currentPage - 1)}
              style={{ padding: '6px 12px', fontSize: '0.825rem', display: 'inline-flex', alignItems: 'center', gap: '4px' }}
            >
              <ChevronLeft size={16} /> Anterior
            </button>

            <div style={{ display: 'flex', alignItems: 'center', gap: '4px', margin: '0 4px' }}>
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
              className="btn btn-outline btn-sm"
              disabled={currentPage >= totalPages || loading}
              onClick={() => handlePageChange(currentPage + 1)}
              style={{ padding: '6px 12px', fontSize: '0.825rem', display: 'inline-flex', alignItems: 'center', gap: '4px' }}
            >
              Siguiente <ChevronRight size={16} />
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
