import React, { useState, useEffect } from 'react';
import { getSalesHistory, getSaleHistoryDetail } from '../services/historyApi';
import { Search, Loader2, Calendar, ChevronRight, ChevronDown, RefreshCw, CheckCircle, Clock, XCircle, FileText } from 'lucide-react';
import { useExchangeRate } from '../context/ExchangeRateContext';
import { formatBsS, formatUSD, formatNumberEs, formatDate, formatTime, formatQuantity } from '../utils/formatters';
import Pagination from '../components/ui/Pagination';
import './HistoryPage.css';

const PAGE_SIZE = 25;

export default function HistoryPage() {
  const { exchangeRate } = useExchangeRate();
  const [sales, setSales] = useState([]);
  const [loading, setLoading] = useState(false);
  // Filtro inicial: solo el día en curso (la tabla oculta los días anteriores
  // hasta que el usuario cambie manualmente el rango). Fecha local en formato YYYY-MM-DD.
  const todayIso = () => {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  };
  const [startDate, setStartDate] = useState(todayIso);
  const [endDate, setEndDate] = useState(todayIso);
  const [searchTerm, setSearchTerm] = useState('');
  const [debouncedSearch, setDebouncedSearch] = useState('');
  const [currentPage, setCurrentPage] = useState(1);
  const [totalCount, setTotalCount] = useState(0);

  const [expandedSaleId, setExpandedSaleId] = useState(null);
  const [saleDetails, setSaleDetails] = useState({});
  const [error, setError] = useState(null);
  const [reloadToken, setReloadToken] = useState(0);

  // Búsqueda multicampo con debounce: al escribir, la vista se actualiza sola
  // (300 ms) y vuelve a la primera página.
  useEffect(() => {
    const timer = setTimeout(() => {
      setDebouncedSearch(searchTerm);
      setCurrentPage(1);
    }, 300);
    return () => clearTimeout(timer);
  }, [searchTerm]);

  // 8.7-M9: un SOLO efecto orquestador con AbortController para la lista de ventas.
  // Los handlers de búsqueda/paginación/fechas solo cambian estado.
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    getSalesHistory(currentPage, PAGE_SIZE, startDate, endDate, debouncedSearch, controller.signal)
      .then((data) => {
        const items = data?.items || data?.Items || (Array.isArray(data) ? data : []);
        const total = data?.totalCount ?? data?.TotalCount ?? items.length;
        setSales(items);
        setTotalCount(total);
      })
      .catch((err) => {
        if (err?.name !== 'AbortError') {
          console.error('[HistoryPage] Error al cargar historial:', err);
          setError('No se pudo cargar el historial de ventas.');
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });

    return () => controller.abort();
  }, [currentPage, startDate, endDate, debouncedSearch, reloadToken]);

  const handleSearchClick = () => {
    setCurrentPage(1);
  };

  const handlePageChange = (newPage) => {
    const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));
    if (newPage < 1 || newPage > totalPages || newPage === currentPage) return;
    setCurrentPage(newPage);
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
      } catch {
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
            onClick={() => setReloadToken((t) => t + 1)}
            disabled={loading}
          >
            <RefreshCw size={16} className={loading ? 'animate-spin' : ''} /> Actualizar
          </button>
        </div>
      </div>

      {/* ── Filtros de Fecha y Búsqueda ── */}
      <div className="card mb-4 history-filter-card">
        <div className="history-filter-row">
          <div className="history-filter-item history-search-col">
            <label className="history-filter-label">Buscar (Factura / Cliente / Cajero)</label>
            <div className="history-input-with-icon">
              <Search size={16} className="history-input-icon" />
              <input
                type="text"
                className="history-filter-input history-search-input"
                placeholder="N° de factura, cliente o cajero..."
                value={searchTerm}
                onChange={(e) => setSearchTerm(e.target.value)}
              />
            </div>
          </div>

          <div className="history-filter-item history-date-col">
            <label className="history-filter-label">Fecha Inicio</label>
            <input
              type="date"
              className="history-filter-input history-date-input"
              value={startDate}
              onChange={(e) => setStartDate(e.target.value)}
            />
          </div>

          <div className="history-filter-item history-date-col">
            <label className="history-filter-label">Fecha Fin</label>
            <input
              type="date"
              className="history-filter-input history-date-input"
              value={endDate}
              onChange={(e) => setEndDate(e.target.value)}
            />
          </div>

          <div className="history-filter-btn-group">
            <button type="button" className="btn btn-primary history-search-btn" onClick={handleSearchClick}>
              <Search size={16} /> Buscar
            </button>
          </div>
        </div>
      </div>

      {error && <div className="alert alert-danger mb-4">{error}</div>}

      {/* ── Contenido del Historial (Escritorio vs Móvil) ── */}
      <div className="card padding-none overflow-hidden hist-table-card">
        {loading ? (
          <div className="flex-center hist-state-pad">
            <Loader2 className="animate-spin mb-2 mx-auto" size={28} />
            <div>Cargando historial de ventas...</div>
          </div>
        ) : sales.length === 0 ? (
          <div className="text-center hist-state-pad text-muted">
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
                    className="history-mobile-card d-flex flex-column mb-3 hist-m-card"
                  >

                    {/* Piso Superior: Identificación y Estado */}
                    <div
                      onClick={() => toggleExpand(sale.id)}
                      className="d-flex flex-between flex-align-center pb-2 border-bottom cursor-pointer"
                    >
                      <div className="d-flex flex-align-center font-bold hist-sale-id-row">
                        {isExpanded ? <ChevronDown size={18} /> : <ChevronRight size={18} />}
                        <span className="text-primary">N° {invoiceNum}</span>
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
                    <div className="d-flex flex-column pb-2 border-bottom hist-mid-row">
                      {/* Cliente + Cédula en su propia línea */}
                      <div>
                        <div className="d-flex flex-align-center hist-gap-6">
                          <span className="text-label">Cliente:</span>
                          <span
                            className="text-primary font-semibold text-truncate hist-truncate-200"
                            title={customerName}
                          >
                            {customerName}
                          </span>
                        </div>
                        <div className="text-label font-mono ml-4">
                          {sale.customerCedula || 'V-00000000'}
                        </div>
                      </div>

                      {/* Cajero */}
                      <div className="d-flex flex-align-center hist-gap-6">
                        <span className="text-label">Cajero:</span>
                        <span className="text-primary font-semibold">{cashierName}</span>
                      </div>

                      {/* Fecha (Sólo fecha plana sin hora) */}
                      <div className="d-flex flex-align-center hist-gap-6">
                        <span className="text-label">Fecha:</span>
                        <span className="text-primary font-semibold font-mono">{dateOnlyStr}</span>
                      </div>
                    </div>

                    {/* Piso Inferior: Finanzas Resaltadas */}
                    <div className="d-flex flex-between flex-align-center w-full hist-fin-row">
                      <div>
                        <span className="text-label hist-total-label">Total USD:</span>
                        <span className="text-primary font-semibold font-mono text-sm">{formatUSD(sale.totalUSD || 0)}</span>
                      </div>

                      <div className="text-right">
                        <span className="text-label hist-total-label">Total Bs.S:</span>
                        <span className="text-primary font-semibold font-mono hist-total-amnt">{formatNumberEs(totalBsS)}</span>
                      </div>
                    </div>

                    {/* Fila Desplegable de Detalle en Móvil */}
                    {isExpanded && (
                      <div className="border-top-dashed pt-3 hist-fs-08">
                        {detailState?.loading ? (
                          <div className="flex-center p-3 text-muted">
                            <Loader2 className="animate-spin mr-2" size={18} /> Cargando detalles...
                          </div>
                        ) : detailState?.error ? (
                          <div className="alert alert-danger text-xs p-2">{detailState.error}</div>
                        ) : detailState?.data ? (
                          <div className="d-flex flex-column hist-gap-12">

                            <div className="hist-invoice-box">
                              <div className="font-bold text-xs mb-1">
                                Factura N° {detailState.data.invoiceNumber || sale.id}
                              </div>
                              <div className="text-label font-mono hist-fs-07">
                                Hora de Emisión: <strong className="text-primary font-semibold">{new Date(detailState.data.date || sale.date).toLocaleTimeString('es-VE', { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: true })}</strong>
                              </div>
                            </div>

                            <div>
                              <h4 className="font-bold text-xs hist-h4-sm">📦 Artículos Vendidos ({detailState.data.items?.length || 0})</h4>
                              <div className="border hist-radius-8 overflow-hidden">
                                <table className="cart-table w-full hist-fs-07 hist-cell-pad">
                                  <thead>
                                    <tr>
                                      <th className="text-secondary">Producto</th>
                                      <th className="text-right text-secondary">Cant.</th>
                                      <th className="text-right text-secondary">Subtotal Bs.S</th>
                                    </tr>
                                  </thead>
                                  <tbody>
                                    {(detailState.data.items || []).map((item) => (
                                      <tr key={item.id}>
                                        <td className="font-medium">{item.productName}</td>
                                        <td className="text-right font-mono">{formatQuantity(item.quantity)}</td>
                                        <td className="text-right font-mono font-bold">{formatBsS(item.subtotalBsS)}</td>
                                      </tr>
                                    ))}
                                  </tbody>
                                </table>
                              </div>
                            </div>

                            {detailState.data.payments?.length > 0 && (
                              <div>
                                <h4 className="font-bold text-xs hist-h4-sm">💳 Métodos de Pago</h4>
                                <div className="d-flex flex-column gap-1">
                                  {detailState.data.payments.map((pay, idx) => (
                                    <div
                                      key={idx}
                                      className="d-flex flex-between flex-align-center border hist-pay-item"
                                    >
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
                    <th className="text-left text-secondary font-semibold text-nowrap">N° Factura</th>
                    <th className="text-secondary font-semibold text-nowrap">Cliente</th>
                    <th className="text-secondary font-semibold text-nowrap">Cajero</th>
                    <th className="text-right text-secondary font-semibold text-nowrap hist-pr-16">Total Bs.S</th>
                    <th className="text-center text-secondary font-semibold text-nowrap">Estado</th>
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
                          <td className="text-nowrap">
                            <div className="d-flex flex-align-center gap-2">
                              {isExpanded ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
                              <span className="font-bold font-mono text-primary">
                                N° {sale.invoiceNumber ? sale.invoiceNumber.toString().padStart(6, '0') : sale.id}
                              </span>
                            </div>
                          </td>

                          <td className="text-nowrap hist-truncate-180">
                            <div
                              className="font-medium text-truncate hist-truncate-180"
                              title={customerName}
                            >
                              {customerName}
                            </div>
                            <div className="text-xs text-muted">
                              {sale.customerCedula || 'V-00000000'}
                            </div>
                          </td>

                          <td className="text-nowrap">
                            <span className="font-medium">{sale.cashierName || 'Usuario Desconocido'}</span>
                          </td>

                          <td className="text-right text-nowrap hist-pr-16">
                            <div className="font-mono font-bold text-primary">{formatBsS(totalBsS)}</div>
                            <div className="text-xs text-muted">
                              {formatUSD(sale.totalUSD || 0)}
                            </div>
                          </td>

                          {/* Etiqueta Visual de Estado */}
                          <td className="text-center text-nowrap">
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
                            <td colSpan={5} className="history-detail-cell border-top-dashed">
                              {detailState?.loading ? (
                                <div className="flex-center p-3 text-muted">
                                  <Loader2 className="animate-spin mr-2" size={18} /> Cargando detalles de la factura...
                                </div>
                              ) : detailState?.error ? (
                                <div className="alert alert-danger text-sm">{detailState.error}</div>
                              ) : detailState?.data ? (
                                <>
                                  <div className="d-flex flex-align-center hist-detail-date-row">
                                    <Calendar size={16} className="flex-shrink-0" />
                                    <span className="font-semibold">
                                      {formatDate(detailState.data.date)} — {formatTime(detailState.data.date)}
                                    </span>
                                  </div>
                                  <div className="grid hist-detail-grid gap-4">

                                  <div>
                                    <h4 className="font-bold mb-2 hist-h4-lg">Artículos Vendidos ({detailState.data.items?.length || 0})</h4>
                                    <div className="border hist-scroll-x">
                                      <table className="cart-table w-full hist-fs-08">
                                        <thead>
                                          <tr>
                                            <th className="text-left text-secondary font-semibold text-nowrap">Producto</th>
                                            <th className="text-right text-secondary font-semibold text-nowrap">Cant.</th>
                                            <th className="text-right text-secondary font-semibold text-nowrap">P. Unidad</th>
                                            <th className="text-right text-secondary font-semibold text-nowrap">Subtotal Bs.S</th>
                                          </tr>
                                        </thead>
                                        <tbody>
                                          {(detailState.data.items || []).map((item) => (
                                            <tr key={item.id}>
                                              <td className="font-medium">{item.productName}</td>
                                               <td className="text-right font-mono">{formatQuantity(item.quantity)}</td>
                                              <td className="text-right font-mono">{formatBsS(item.unitPriceBsS)}</td>
                                              <td className="text-right font-mono font-bold">{formatBsS(item.subtotalBsS)}</td>
                                            </tr>
                                          ))}
                                        </tbody>
                                      </table>
                                    </div>
                                  </div>

                                  <div>
                                    <h4 className="font-bold mb-2 hist-h4-lg">Resumen Financiero</h4>
                                    <div className="border text-sm p-3 hist-summary-box">
                                      <div className="d-flex flex-between mb-1">
                                        <span className="text-muted">Cliente:</span>
                                        <span className="font-bold">{detailState.data.customerName || 'Consumidor Final'}</span>
                                      </div>
                                      <div className="d-flex flex-between mb-1">
                                        <span className="text-muted">Cédula / RIF:</span>
                                        <span className="font-bold">{detailState.data.customerCedula || 'V-00000000'}</span>
                                      </div>
                                      <div className="d-flex flex-between mb-1">
                                        <span className="text-muted">Tasa de Cambio:</span>
                                        <span className="font-bold">Bs.S {formatNumberEs(detailState.data.appliedRate)}</span>
                                      </div>
                                      <div className="d-flex flex-between mb-1">
                                        <span className="text-muted">Total USD:</span>
                                        <span className="font-bold">{formatUSD(detailState.data.totalUSD || 0)}</span>
                                      </div>
                                      <div className="d-flex flex-between">
                                        <span className="text-muted">Total Bs.S:</span>
                                        <span className="font-bold text-primary">{formatBsS(detailState.data.totalBsS || 0)}</span>
                                      </div>
                                    </div>

                                    <h4 className="font-bold mb-2 hist-h4-lg">Métodos de Pago</h4>
                                    {detailState.data.payments?.length > 0 ? (
                                      <div className="d-flex flex-column gap-2">
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
        <Pagination
          currentPage={currentPage}
          totalPages={totalPages}
          totalCount={totalCount}
          onPageChange={handlePageChange}
          loading={loading}
          itemLabel="pedidos"
        />
      )}
    </div>
  );
}
