import { useState, useEffect, useCallback } from 'react';
import { api } from '../services/api';
import { 
  Landmark, 
  ArrowUpRight, 
  ArrowDownLeft, 
  FastForward, 
  Loader2, 
  RefreshCw, 
  ShieldAlert, 
  TrendingUp, 
  TrendingDown, 
  Clock,
  CheckCircle
} from 'lucide-react';
import { useExchangeRate } from '../context/ExchangeRateContext';
import { useAuth } from '../context/AuthContext';
import { formatBsS, formatUSD, formatNumberEs } from '../utils/formatters';

import CashInModal from '../components/register/CashInModal';
import CashOutModal from '../components/register/CashOutModal';
import CashAdvanceModal from '../components/register/CashAdvanceModal';

export default function RegisterPage() {
  const { exchangeRate } = useExchangeRate();
  const { user } = useAuth();
  
  const isAdmin = user?.role === 0 || user?.role === 'Admin' || user?.role === '0';

  const [session, setSession] = useState(null);
  const [historyTransactions, setHistoryTransactions] = useState([]);
  const [loading, setLoading] = useState(false);

  // Discrete filter states for movements
  const [typeFilter, setTypeFilter] = useState('all'); // 'all', 'income', 'expense'
  const [sourceFilter, setSourceFilter] = useState('all');

  // Pagination state for movements table
  const [currentPage, setCurrentPage] = useState(1);
  const ITEMS_PER_PAGE = 25;

  // Reset pagination to page 1 whenever filters change
  useEffect(() => {
    setCurrentPage(1);
  }, [typeFilter, sourceFilter]);

  // Modal open states
  const [isCashInOpen, setIsCashInOpen] = useState(false);
  const [isCashOutOpen, setIsCashOutOpen] = useState(false);
  const [isCashAdvanceOpen, setIsCashAdvanceOpen] = useState(false);

  const loadSession = useCallback(async () => {
    setLoading(true);
    try {
      const [sessionData, historyData] = await Promise.all([
        api.get('/api/cashdrawer/active-session'),
        api.get('/api/cashdrawer/history').catch(() => []),
      ]);
      setSession(sessionData);
      setHistoryTransactions(historyData || []);
    } catch (err) {
      console.error('[RegisterPage] Error al obtener sesión de caja:', err);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadSession();
  }, [loadSession]);

  // Derived state calculations
  const transactions = session?.transactions || [];
  const openingBsS = session?.openingBalanceLocal || 0;
  
  // Physical cash income & expense totals
  const totalIncomeBsS = transactions
    .filter(t => t.type === 0 && t.source !== 0 && t.isPhysicalCash)
    .reduce((sum, t) => sum + (t.amountLocal || 0), 0);

  const totalExpenseBsS = transactions
    .filter(t => t.type === 1 && t.isPhysicalCash)
    .reduce((sum, t) => sum + (t.amountLocal || 0), 0);

  const expectedCashBsS = openingBsS + totalIncomeBsS - totalExpenseBsS;
  const expectedCashUsd = (exchangeRate && exchangeRate > 0) ? expectedCashBsS / exchangeRate : 0;

  // Last 7 received incomes (sorted most recent first)
  const recentIncomes = [...transactions]
    .filter(t => t.type === 0 && t.source !== 0 && t.isPhysicalCash)
    .sort((a, b) => new Date(b.transactionTimeLocal || b.transactionTime) - new Date(a.transactionTimeLocal || a.transactionTime))
    .slice(0, 7);

  // Historial completo de movimientos físicos (sesión activa + sesiones anteriores):
  // se conserva tras el cierre de caja para mantener la trazabilidad de auditoría.
  // La tabla de movimientos usa este historial; las tarjetas de resumen (INGRESOS/EGRESOS/ESPERADO)
  // siguen calculándose únicamente con la sesión activa, que inicia con acumuladores limpios.
  const orderedTransactions = [...historyTransactions]
    .filter(t => t.isPhysicalCash)
    .sort((a, b) => new Date(b.transactionTimeLocal || b.transactionTime) - new Date(a.transactionTimeLocal || a.transactionTime));

  // Discrete filtered movements
  const filteredTransactions = orderedTransactions.filter((tx) => {
    if (typeFilter === 'income' && tx.type !== 0) return false;
    if (typeFilter === 'expense' && tx.type !== 1) return false;

    if (sourceFilter === 'opening' && tx.source !== 0) return false;
    if (sourceFilter === 'sale' && tx.source !== 1) return false;
    if (sourceFilter === 'advance' && tx.source !== 4) return false;
    if (sourceFilter === 'cashin' && tx.source !== 5) return false;
    if (sourceFilter === 'cashout' && tx.source !== 6) return false;

    return true;
  });

  // Pagination derived calculations
  const totalPages = Math.ceil(filteredTransactions.length / ITEMS_PER_PAGE) || 1;
  const startIndex = (currentPage - 1) * ITEMS_PER_PAGE;
  const paginatedTransactions = filteredTransactions.slice(startIndex, startIndex + ITEMS_PER_PAGE);

  function getSourceLabel(source) {
    switch (source) {
      case 0:
      case 'Opening':
        return 'Apertura';
      case 1:
      case 'SalePayment':
      case 'Sale':
        return 'Venta POS';
      case 2:
      case 'ManualAdjustment':
        return 'Ajuste Manual';
      case 3:
      case 'Closing':
        return 'Cierre Caja';
      case 4:
      case 'CashAdvance':
        return 'Adelanto Efectivo';
      case 5:
      case 'CashIn':
        return 'Ingreso de Caja';
      case 6:
      case 'CashOut':
        return 'Retiro de Caja';
      default:
        return typeof source === 'string' ? source : 'Movimiento';
    }
  }

  function formatTime(timeStr) {
    if (!timeStr) return '-';
    try {
      const d = new Date(timeStr);
      return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    } catch {
      return timeStr;
    }
  }

  return (
    <div className="register-page container-fluid p-4" style={{ maxWidth: '1200px', margin: '0 auto' }}>
      
      {/* ── Requisito 1: Reestructuración del Encabezado (Título + Sesión/Tasa + Cuadrícula de Botones Táctiles en Móvil) ── */}
      <div className="register-header-container mb-4">
        <div className="register-header-info mb-3">
          <h2 className="register-main-title font-bold text-2xl mb-1 flex-align-center gap-2">
            <Landmark size={28} className="color-primary flex-shrink-0" />
            <span>Estado y Movimientos de Caja Registradora</span>
          </h2>
          <div className="register-session-meta flex-align-center gap-2 flex-wrap">
            {session ? (
              <span className="badge badge-success flex-align-center gap-1">
                <span className="dot dot-success animate-pulse"></span> SESIÓN ACTIVA (N° {session.id})
              </span>
            ) : (
              <span className="badge badge-secondary">SIN SESIÓN ABIERTA</span>
            )}
            <span className="text-xs text-muted">Tasa Actual: {formatBsS(exchangeRate || 0)} / USD</span>
          </div>
        </div>

        {/* Requisito 1: Botones de Acción Reubicados en Cuadrícula Táctil para Móvil */}
        <div className="register-actions-grid">
          <button
            type="button"
            className="btn btn-outline flex-align-center justify-content-center gap-2 register-action-btn"
            onClick={loadSession}
            disabled={loading}
          >
            <RefreshCw size={16} className={loading ? 'animate-spin' : ''} /> Actualizar
          </button>

          <button
            type="button"
            className={`btn ${isAdmin ? 'btn-success' : 'btn-outline text-muted'} flex-align-center justify-content-center gap-2 register-action-btn`}
            onClick={() => isAdmin ? setIsCashInOpen(true) : null}
            disabled={!session || !isAdmin}
            title={isAdmin ? 'Ingresar dinero a caja' : 'Solo usuarios Administradores'}
          >
            <ArrowDownLeft size={18} /> CASH IN {!isAdmin && <span className="text-xs font-normal opacity-75">(Admin)</span>}
          </button>

          <button
            type="button"
            className={`btn ${isAdmin ? 'btn-danger' : 'btn-outline text-muted'} flex-align-center justify-content-center gap-2 register-action-btn`}
            onClick={() => isAdmin ? setIsCashOutOpen(true) : null}
            disabled={!session || !isAdmin}
            title={isAdmin ? 'Retirar dinero de caja' : 'Solo usuarios Administradores'}
          >
            <ArrowUpRight size={18} /> CASH OUT {!isAdmin && <span className="text-xs font-normal opacity-75">(Admin)</span>}
          </button>

          <button
            type="button"
            className="btn btn-primary flex-align-center justify-content-center gap-2 register-action-btn"
            onClick={() => setIsCashAdvanceOpen(true)}
            disabled={!session}
          >
            <FastForward size={18} /> Adelanto Efectivo
          </button>
        </div>
      </div>

      {/* ── Requisito 2: Aviso de Acceso Limitado (Alineado al Inicio Superior con la Primera Línea del Texto) ── */}
      {!isAdmin && (
        <div 
          className="alert alert-warning mb-4 text-sm p-3 border-warning bg-warning-light"
          style={{ display: 'flex', alignItems: 'flex-start', gap: '10px' }}
        >
          <ShieldAlert size={18} className="color-warning flex-shrink-0" style={{ marginTop: '2px' }} />
          <span>Acceso limitado: Los botones <strong>CASH IN</strong> y <strong>CASH OUT</strong> están restringidos a usuarios con rol <strong>Administrador</strong>.</span>
        </div>
      )}

      {loading && !session ? (
        <div className="p-5 text-center text-muted card shadow-sm">
          <Loader2 className="animate-spin mb-2 inline-block" size={32} />
          <div>Cargando datos de caja registradora...</div>
        </div>
      ) : (
        <>
          {/* ── Requisito 3: Tarjetas de Resumen (Espaciado Tipográfico Garantizado entre Número y Divisa Bs.S) ── */}
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(280px, 1fr))', gap: '16px', width: '100%', marginBottom: '20px' }}>
            
            {/* Tarjeta 1: EFECTIVO ESPERADO EN CAJA */}
            <div className="card shadow-sm p-4 bg-surface flex-between flex-align-center" style={{ borderLeft: '4px solid var(--primary-color, #6366f1)', backgroundColor: 'rgba(99, 102, 241, 0.04)' }}>
              <div className="flex-1 pr-3 flex flex-column justify-content-between">
                <span className="stat-label text-xs font-bold text-muted uppercase tracking-wider block mb-1">
                  EFECTIVO ESPERADO EN CAJA
                </span>
                
                {/* Requisito 3: Espaciado en la divisa (26,673.74 Bs.S) */}
                <div className="stat-val mb-1 flex-align-baseline gap-2" style={{ whiteSpace: 'nowrap' }}>
                  <span className="text-3xl font-extrabold color-primary">
                    {formatNumberEs(expectedCashBsS, 2)}
                  </span>
                  <span className="text-base font-bold color-primary" style={{ marginLeft: '6px' }}>
                    Bs.S
                  </span>
                </div>

                <div className="text-xs font-semibold text-muted">
                  $ {formatNumberEs(expectedCashUsd, 2)} USD
                </div>
              </div>

              <div className="flex-align-center justify-content-center p-2 rounded-lg flex-shrink-0" style={{ width: '48px', height: '48px', backgroundColor: 'rgba(99, 102, 241, 0.12)' }}>
                <Landmark size={26} className="color-primary" />
              </div>
            </div>

            {/* Tarjeta 2: TOTAL INGRESOS EN EFECTIVO */}
            <div className="card shadow-sm p-4 bg-surface flex flex-column justify-content-between" style={{ borderLeft: '4px solid #10B981', backgroundColor: 'rgba(16, 185, 129, 0.04)' }}>
              <div className="flex-align-center gap-2 mb-1">
                <TrendingUp size={18} className="color-success flex-shrink-0" />
                <span className="stat-label text-xs font-bold uppercase tracking-wider" style={{ color: '#059669' }}>
                  TOTAL INGRESOS EN EFECTIVO
                </span>
              </div>

              {/* Requisito 3: Espaciado en la divisa */}
              <div className="stat-val mb-1 flex-align-baseline gap-2" style={{ whiteSpace: 'nowrap' }}>
                <span className="text-2xl font-extrabold color-success">
                  {formatNumberEs(totalIncomeBsS, 2)}
                </span>
                <span className="text-sm font-bold color-success" style={{ marginLeft: '6px' }}>
                  Bs.S
                </span>
              </div>

              <div className="text-xs text-muted font-medium">
                Acumulado en sesión
              </div>
            </div>

            {/* Tarjeta 3: TOTAL EGRESOS EN EFECTIVO */}
            <div className="card shadow-sm p-4 bg-surface flex flex-column justify-content-between" style={{ borderLeft: '4px solid #EF4444', backgroundColor: 'rgba(239, 68, 68, 0.04)' }}>
              <div className="flex-align-center gap-2 mb-1">
                <TrendingDown size={18} className="color-danger flex-shrink-0" />
                <span className="stat-label text-xs font-bold uppercase tracking-wider" style={{ color: '#DC2626' }}>
                  TOTAL EGRESOS EN EFECTIVO
                </span>
              </div>

              {/* Requisito 3: Espaciado en la divisa */}
              <div className="stat-val mb-1 flex-align-baseline gap-2" style={{ whiteSpace: 'nowrap' }}>
                <span className="text-2xl font-extrabold color-danger">
                  {formatNumberEs(totalExpenseBsS, 2)}
                </span>
                <span className="text-sm font-bold color-danger" style={{ marginLeft: '6px' }}>
                  Bs.S
                </span>
              </div>

              <div className="text-xs text-muted font-medium">
                Retiros y salidas
              </div>
            </div>

          </div>

          <div className="grid grid-1 gap-4">

            {/* CARRUSEL DE ÚLTIMOS INGRESOS */}
            <div className="card shadow-sm p-4 bg-surface mb-4 w-100 max-w-100 overflow-hidden" style={{ width: '100%', maxWidth: '100%' }}>
              <div className="flex-between flex-align-center mb-4">
                <h3 className="card-title text-base font-bold flex-align-center gap-2 m-0 color-success">
                  <CheckCircle size={20} className="color-success" /> ÚLTIMOS INGRESOS RECIBIDOS
                </h3>
                <span className="badge badge-success text-xs font-medium">
                  {recentIncomes.length} de máx. 7
                </span>
              </div>

              {recentIncomes.length === 0 ? (
                <div className="p-4 text-center text-muted border border-dashed rounded">
                  No hay ingresos registrados en esta sesión.
                </div>
              ) : (
                <div
                  className="recent-incomes-scroll-container flex gap-3.5 py-1 custom-scrollbar w-100"
                  style={{
                    overflowX: 'auto',
                    overflowY: 'hidden',
                    scrollSnapType: 'x mandatory',
                    WebkitOverflowScrolling: 'touch'
                  }}
                >
                  {recentIncomes.map((tx, idx) => {
                    const invoiceTitle = (tx.sale?.invoiceNumber || tx.invoiceNumber)
                      ? `Factura N° ${tx.sale?.invoiceNumber || tx.invoiceNumber}`
                      : (tx.description || getSourceLabel(tx.source));

                    const amountUsd = tx.amountUsd || ((exchangeRate && exchangeRate > 0) ? tx.amountLocal / exchangeRate : 0);

                    return (
                      <div
                        key={tx.id || idx}
                        className="recent-income-card rounded-lg border flex flex-column justify-content-between shadow-xs flex-shrink-0 p-3"
                        style={{
                          flex: '0 0 220px',
                          minWidth: '220px',
                          scrollSnapAlign: 'start',
                          textAlign: 'left'
                        }}
                      >
                        <div>
                          <div
                            className="font-bold text-sm text-truncate mb-1.5 text-left w-100"
                            style={{ whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}
                            title={invoiceTitle}
                          >
                            {invoiceTitle}
                          </div>

                          <div className="text-xs text-muted mb-3 flex-align-center gap-1.5 w-100 text-left">
                            <Clock size={13} className="flex-shrink-0" style={{ marginRight: '4px' }} />
                            <span>{formatTime(tx.transactionTimeLocal || tx.transactionTime)}</span>
                          </div>
                        </div>

                        <div className="flex-align-baseline gap-2 pt-2 border-top w-100 flex-wrap text-left">
                          <span className="font-extrabold text-sm color-success" style={{ whiteSpace: 'nowrap' }}>
                            {formatBsS(tx.amountLocal)}
                          </span>
                          <span className="text-xs text-muted font-medium" style={{ whiteSpace: 'nowrap' }}>
                            &nbsp;({formatUSD(amountUsd)})
                          </span>
                        </div>
                      </div>
                    );
                  })}
                </div>
              )}
            </div>

            {/* ── ÁREA 3: TABLA DE MOVIMIENTOS FÍSICOS ── */}
            <div className="card shadow-sm p-4 bg-surface overflow-hidden w-100 max-w-100" style={{ width: '100%', maxWidth: '100%' }}>
              
              {/* ── Requisito 4: Cabecera en 2 Niveles para Móvil (Título arriba, Selectores 50% abajo) ── */}
              <div className="register-table-header-container mb-3">
                <h3 className="register-table-title card-title text-base font-bold flex-align-center gap-2 m-0">
                  <Landmark size={20} className="color-primary flex-shrink-0" /> 
                  <span>Movimientos de Caja (Físicos)</span>
                </h3>

                {/* Selectores desplegables al 50% cada uno en móvil */}
                <div className="register-table-filters-container">
                  <select
                    className="form-select form-select-sm register-filter-select"
                    value={typeFilter}
                    onChange={(e) => setTypeFilter(e.target.value)}
                    title="Filtrar por Tipo"
                  >
                    <option value="all">Todos los Tipos</option>
                    <option value="income">Solo Ingresos (+)</option>
                    <option value="expense">Solo Egresos (-)</option>
                  </select>

                  <select
                    className="form-select form-select-sm register-filter-select"
                    value={sourceFilter}
                    onChange={(e) => setSourceFilter(e.target.value)}
                    title="Filtrar por Origen"
                  >
                    <option value="all">Todos los Orígenes</option>
                    <option value="sale">Venta POS</option>
                    <option value="cashin">Ingreso de Caja</option>
                    <option value="cashout">Retiro de Caja</option>
                    <option value="advance">Adelanto Efectivo</option>
                    <option value="opening">Apertura</option>
                  </select>

                  {(typeFilter !== 'all' || sourceFilter !== 'all') && (
                    <button
                      type="button"
                      className="btn btn-outline btn-sm text-xs py-0 px-2"
                      onClick={() => {
                        setTypeFilter('all');
                        setSourceFilter('all');
                      }}
                      title="Restablecer Filtros"
                    >
                      Limpiar
                    </button>
                  )}

                  <span className="badge badge-outline text-xs register-count-badge">
                    {filteredTransactions.length} de {orderedTransactions.length}
                  </span>
                </div>
              </div>

              {/* ── Requisito 5: Rescate de Tabla de Movimientos (Scroll Horizontal + whitespace-nowrap) ── */}
              {filteredTransactions.length === 0 ? (
                <div className="p-4 text-center text-muted border border-dashed rounded">
                  {orderedTransactions.length === 0 
                    ? 'No se registran movimientos de caja.'
                    : 'No se encontraron movimientos con los filtros seleccionados.'}
                </div>
              ) : (
                <>
                  <div className="w-100 overflow-x-auto custom-scrollbar border rounded history-table-wrapper" style={{ width: '100%', maxWidth: '100%', overflowX: 'auto', WebkitOverflowScrolling: 'touch' }}>
                    <table className="table table-hover w-100 m-0 cart-table history-main-table" style={{ minWidth: '780px' }}>
                      <thead className="sticky-header">
                        <tr className="bg-light">
                          <th style={{ width: '100px', whiteSpace: 'nowrap', position: 'sticky', top: 0, zIndex: 10, backgroundColor: 'var(--bg-surface)' }}>Hora</th>
                          <th style={{ width: '105px', whiteSpace: 'nowrap', position: 'sticky', top: 0, zIndex: 10, backgroundColor: 'var(--bg-surface)' }}>Tipo</th>
                          <th style={{ width: '135px', whiteSpace: 'nowrap', position: 'sticky', top: 0, zIndex: 10, backgroundColor: 'var(--bg-surface)' }}>Origen</th>
                          <th style={{ minWidth: '180px', whiteSpace: 'nowrap', position: 'sticky', top: 0, zIndex: 10, backgroundColor: 'var(--bg-surface)' }}>Concepto / Detalle</th>
                          <th className="text-right" style={{ width: '140px', textAlign: 'right', paddingRight: '16px', position: 'sticky', top: 0, zIndex: 10, backgroundColor: 'var(--bg-surface)', whiteSpace: 'nowrap' }}>
                            MONTO (BS.S)
                          </th>
                          <th className="text-right" style={{ width: '120px', textAlign: 'right', paddingRight: '16px', position: 'sticky', top: 0, zIndex: 10, backgroundColor: 'var(--bg-surface)', whiteSpace: 'nowrap' }}>
                            EQUIV. (USD)
                          </th>
                        </tr>
                      </thead>
                      <tbody>
                        {paginatedTransactions.map((tx, idx) => {
                          const isIncome = tx.type === 0;
                          const amountUsd = tx.amountUsd || ((exchangeRate && exchangeRate > 0) ? tx.amountLocal / exchangeRate : 0);
                          const conceptStr = tx.description || getSourceLabel(tx.source);

                          return (
                            <tr key={tx.id || idx}>
                              {/* Hora (Bloqueo de salto de línea) */}
                              <td className="font-mono text-xs font-medium text-muted" style={{ whiteSpace: 'nowrap' }}>
                                {formatTime(tx.transactionTimeLocal || tx.transactionTime)}
                              </td>

                              {/* Tipo Badge */}
                              <td style={{ whiteSpace: 'nowrap', width: '105px' }}>
                                <span 
                                  className={`badge ${isIncome ? 'badge-success' : 'badge-danger'} text-xs font-bold`}
                                  style={{ width: '90px', display: 'inline-flex', justifyContent: 'center', textAlign: 'center' }}
                                >
                                  {isIncome ? 'INGRESO' : 'EGRESO'}
                                </span>
                              </td>

                              {/* Origen */}
                              <td style={{ whiteSpace: 'nowrap' }}>
                                <span className="badge badge-outline text-xs font-mono">
                                  {getSourceLabel(tx.source)}
                                </span>
                              </td>

                              {/* Concepto / Detalle (Truncado sin romper fila) */}
                              <td
                                className="text-sm font-medium text-truncate"
                                style={{
                                  maxWidth: '220px',
                                  whiteSpace: 'nowrap',
                                  overflow: 'hidden',
                                  textOverflow: 'ellipsis'
                                }}
                                title={conceptStr}
                              >
                                {conceptStr}
                              </td>

                              {/* Monto Bs.S (Alineado a la derecha) */}
                              <td className={`text-right font-extrabold text-sm ${isIncome ? 'color-success' : 'color-danger'}`} style={{ textAlign: 'right', whiteSpace: 'nowrap', paddingRight: '16px' }}>
                                {isIncome ? '+' : '-'}{formatBsS(tx.amountLocal)}
                              </td>

                              {/* Equiv USD (Alineado a la derecha) */}
                              <td className="text-right text-xs text-muted font-medium" style={{ textAlign: 'right', whiteSpace: 'nowrap', paddingRight: '16px' }}>
                                {formatUSD(amountUsd)}
                              </td>
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>

                  {/* Pie de Paginación */}
                  <div className="flex-between flex-align-center mt-3 pt-3 border-top text-xs flex-wrap gap-2 w-100">
                    <span className="text-muted">
                      Mostrando <strong>{startIndex + 1}</strong> a <strong>{Math.min(startIndex + ITEMS_PER_PAGE, filteredTransactions.length)}</strong> de <strong>{filteredTransactions.length}</strong> movimientos
                    </span>

                    {totalPages > 1 && (
                      <div className="flex-align-center gap-1">
                        <button
                          type="button"
                          className="btn btn-outline btn-sm py-1 px-2 text-xs"
                          onClick={() => setCurrentPage(prev => Math.max(prev - 1, 1))}
                          disabled={currentPage === 1}
                        >
                          Anterior
                        </button>
                        <span className="px-2 font-semibold">
                          Página {currentPage} de {totalPages}
                        </span>
                        <button
                          type="button"
                          className="btn btn-outline btn-sm py-1 px-2 text-xs"
                          onClick={() => setCurrentPage(prev => Math.min(prev + 1, totalPages))}
                          disabled={currentPage === totalPages}
                        >
                          Siguiente
                        </button>
                      </div>
                    )}
                  </div>
                </>
              )}
            </div>

          </div>
        </>
      )}

      {/* Modales de Operaciones */}
      <CashInModal
        isOpen={isCashInOpen}
        onClose={() => setIsCashInOpen(false)}
        sessionId={session?.id}
        exchangeRate={exchangeRate}
        user={user}
        onSuccess={loadSession}
      />

      <CashOutModal
        isOpen={isCashOutOpen}
        onClose={() => setIsCashOutOpen(false)}
        sessionId={session?.id}
        availableCashBsS={expectedCashBsS}
        exchangeRate={exchangeRate}
        user={user}
        onSuccess={loadSession}
      />

      <CashAdvanceModal
        isOpen={isCashAdvanceOpen}
        onClose={() => setIsCashAdvanceOpen(false)}
        sessionId={session?.id}
        availableCashBsS={expectedCashBsS}
        exchangeRate={exchangeRate}
        user={user}
        onSuccess={loadSession}
      />
    </div>
  );
}
