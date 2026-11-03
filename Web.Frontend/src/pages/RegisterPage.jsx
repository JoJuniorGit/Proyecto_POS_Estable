import { useState, useEffect, useCallback, useMemo } from 'react';
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
  CheckCircle,
  Wallet,
  Lock,
  AlertCircle
} from 'lucide-react';
import { useExchangeRate } from '../context/ExchangeRateContext';
import { useAuth } from '../context/AuthContext';
import { useMonetaryProfile } from '../context/MonetaryProfileContext';
import { displayCurrencyCode } from '../utils/monetaryProfile';
import { formatNumberEs, formatDate } from '../utils/formatters';
import './RegisterPage.css';

import CashInModal from '../components/register/CashInModal';
import CashOutModal from '../components/register/CashOutModal';
import CashAdvanceModal from '../components/register/CashAdvanceModal';
import PettyCashPanel from '../components/register/PettyCashPanel';
import PettyCashExpenseModal from '../components/register/PettyCashExpenseModal';
import PettyCashCancelModal from '../components/register/PettyCashCancelModal';
import CashDrawerCloseModal from '../components/register/CashDrawerCloseModal';
import CashDrawerOpenModal from '../components/register/CashDrawerOpenModal';
import { getPettyCashExpenses, getPettyCashPhysicalBalances } from '../services/pettyCashApi';
import { canCloseCashDrawerSession, canUsePettyCash, resolveLocalCurrencyProfile } from '../components/register/registerFormLogic';
import Pagination from '../components/ui/Pagination';
import RoleGuard from '../navigation/RoleGuard';
import { normalizeRole } from '../navigation/roleViews';
import { CashTransactionSource, getSourceLabel, matchesSourceFilter } from '../constants/cashTransactionSource';

export default function RegisterPage() {
  return (
    <RoleGuard view="register" message="No tienes permisos para ver el registro de caja.">
      <RegisterPageContent />
    </RoleGuard>
  );
}

function RegisterPageContent() {
  const { exchangeRate } = useExchangeRate();
  const { user } = useAuth();
  const {
    profile,
    primaryCurrencyCode,
    formatPrimary,
    formatSecondary,
  } = useMonetaryProfile();
  const { isDual, label: localCurrencyLabel } = resolveLocalCurrencyProfile(profile);
  const primaryCurrencyLabel = displayCurrencyCode(primaryCurrencyCode);

  const isAdmin = normalizeRole(user?.role) === 'Admin';
  // T5 (Paso 13 / 8.188): roles de la caja chica (Admin/Manager/Cashier) y del cierre de la
  // sesión (solo supervisión). El gate del servidor sigue siendo la autoridad final.
  const role = normalizeRole(user?.role);
  const showPettyCash = canUsePettyCash(role);
  const showCloseCashDrawer = canCloseCashDrawerSession(role);

  const [session, setSession] = useState(null);
  const [historyTransactions, setHistoryTransactions] = useState([]);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState(null);

  // Discrete filter states for movements
  const [typeFilter, setTypeFilter] = useState('all'); // 'all', 'income', 'expense'
  const [sourceFilter, setSourceFilter] = useState('all');

  // Pagination state for movements table
  const [currentPage, setCurrentPage] = useState(1);
  const ITEMS_PER_PAGE = 25;
  const HISTORY_FETCH_LIMIT = 300;

  // Reset pagination to page 1 whenever filters change
  useEffect(() => {
    setCurrentPage(1);
  }, [typeFilter, sourceFilter]);

  // Modal open states
  const [isCashInOpen, setIsCashInOpen] = useState(false);
  const [isCashOutOpen, setIsCashOutOpen] = useState(false);
  const [isCashAdvanceOpen, setIsCashAdvanceOpen] = useState(false);

  // Caja chica del turno (T5): listado, saldos físicos y modales de registro/anulación.
  const [pettyCashExpenses, setPettyCashExpenses] = useState([]);
  const [pettyCashBalances, setPettyCashBalances] = useState(null);
  const [pettyCashLoading, setPettyCashLoading] = useState(false);
  const [isPettyCashPanelOpen, setIsPettyCashPanelOpen] = useState(false);
  const [isPettyCashExpenseOpen, setIsPettyCashExpenseOpen] = useState(false);
  const [cancelTargetExpense, setCancelTargetExpense] = useState(null);
  const [isCloseCashDrawerOpen, setIsCloseCashDrawerOpen] = useState(false);
  const [isOpenCashDrawerOpen, setIsOpenCashDrawerOpen] = useState(false);

  // Los gastos del turno se recargan junto con la sesión (el registro/anulación se reflejan de
  // inmediato). Los saldos físicos por gaveta solo se piden a supervisión: el Cashier conserva el
  // arqueo ciego y el servidor sigue validando la liquidez al registrar.
  const loadPettyCash = useCallback(async (sessionId) => {
    if (!sessionId || !showPettyCash) {
      setPettyCashExpenses([]);
      setPettyCashBalances(null);
      return;
    }

    setPettyCashLoading(true);
    try {
      const rows = await getPettyCashExpenses(sessionId);
      setPettyCashExpenses(Array.isArray(rows) ? rows : []);

      if (showCloseCashDrawer) {
        try {
          setPettyCashBalances(await getPettyCashPhysicalBalances(sessionId));
        } catch (balancesErr) {
          console.warn('[RegisterPage] No se pudieron cargar los saldos físicos de caja:', balancesErr);
          setPettyCashBalances(null);
        }
      } else {
        setPettyCashBalances(null);
      }
    } catch (err) {
      console.warn('[RegisterPage] No se pudieron cargar los gastos de caja chica:', err);
      setPettyCashExpenses([]);
      setPettyCashBalances(null);
    } finally {
      setPettyCashLoading(false);
    }
  }, [showPettyCash, showCloseCashDrawer]);

  const loadSession = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      const [sessionData, historyData] = await Promise.all([
        api.get('/api/cashdrawer/active-session'),
        api.get(`/api/cashdrawer/history?limit=${HISTORY_FETCH_LIMIT}`).catch((err) => {
          console.warn('[RegisterPage] No se pudo cargar historial de caja:', err);
          return [];
        }),
      ]);
      setSession(sessionData);
      setHistoryTransactions(historyData || []);
      await loadPettyCash(sessionData?.id);
    } catch (err) {
      console.error('[RegisterPage] Error al obtener sesión de caja:', err);
      const status = err?.status;
      const message = status === 404 || status === 204
        ? 'No hay una sesión de caja activa.'
        : status >= 500
          ? 'El servidor no está disponible. Intente nuevamente en unos segundos.'
          : (err?.message || 'Error al obtener la información de la sesión de caja');
      setLoadError(message);
    } finally {
      setLoading(false);
    }
  }, [loadPettyCash]);

  useEffect(() => {
    loadSession();
  }, [loadSession]);

  // Derived state calculations
  const transactions = useMemo(() => session?.transactions || [], [session]);
  const openingBsS = session?.openingBalanceLocal || 0;
  
  // Physical cash income & expense totals
  const { totalIncomeBsS, totalExpenseBsS } = useMemo(() => ({
    totalIncomeBsS: transactions
      .filter(t => t.type === 0 && t.source !== CashTransactionSource.Opening && t.isPhysicalCash)
      .reduce((sum, t) => sum + (t.amountLocal || 0), 0),
    totalExpenseBsS: transactions
      .filter(t => t.type === 1 && t.isPhysicalCash)
      .reduce((sum, t) => sum + (t.amountLocal || 0), 0)
  }), [transactions]);

  const expectedCashBsS = openingBsS + totalIncomeBsS - totalExpenseBsS;
  const expectedCashUsd = (exchangeRate && exchangeRate > 0) ? expectedCashBsS / exchangeRate : 0;

  // Last 7 received incomes (sorted most recent first)
  const recentIncomes = useMemo(() => [...transactions]
    .filter(t => t.type === 0 && t.source !== CashTransactionSource.Opening && t.isPhysicalCash)
    .sort((a, b) => new Date(b.transactionTimeLocal || b.transactionTime) - new Date(a.transactionTimeLocal || a.transactionTime))
    .slice(0, 7), [transactions]);

  // Historial completo de movimientos físicos (sesión activa + sesiones anteriores):
  // se conserva tras el cierre de caja para mantener la trazabilidad de auditoría.
  // La tabla de movimientos usa este historial; las tarjetas de resumen (INGRESOS/EGRESOS/ESPERADO)
  // siguen calculándose únicamente con la sesión activa, que inicia con acumuladores limpios.
  const orderedTransactions = useMemo(() => [...historyTransactions]
    .filter(t => t.isPhysicalCash)
    .sort((a, b) => new Date(b.transactionTimeLocal || b.transactionTime) - new Date(a.transactionTimeLocal || a.transactionTime)), [historyTransactions]);

  // Discrete filtered movements
  const filteredTransactions = useMemo(() => orderedTransactions.filter((tx) => {
    if (typeFilter === 'income' && tx.type !== 0) return false;
    if (typeFilter === 'expense' && tx.type !== 1) return false;

    if (!matchesSourceFilter(sourceFilter, tx.source)) return false;

    return true;
  }), [orderedTransactions, typeFilter, sourceFilter]);

  // Pagination derived calculations
  const totalPages = Math.ceil(filteredTransactions.length / ITEMS_PER_PAGE) || 1;
  const startIndex = (currentPage - 1) * ITEMS_PER_PAGE;
  const paginatedTransactions = useMemo(() => filteredTransactions.slice(startIndex, startIndex + ITEMS_PER_PAGE), [filteredTransactions, startIndex]);

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
    <div className="register-page container-fluid p-4 reg-page-container">
      
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
            {isDual && (
              <span className="text-xs text-muted">
                Tasa Actual: {formatSecondary(exchangeRate || 0)} / {primaryCurrencyLabel}
              </span>
            )}
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

          {!session && (
            <button
              type="button"
              className="btn btn-primary flex-align-center justify-content-center gap-2 register-action-btn"
              onClick={() => setIsOpenCashDrawerOpen(true)}
              disabled={loading || !(Number(exchangeRate) > 0)}
              title={`Declarar el fondo inicial en ${localCurrencyLabel}${isDual ? ` y ${primaryCurrencyLabel}` : ''} y abrir la sesión`}
            >
              <Landmark size={18} /> ABRIR CAJA
            </button>
          )}

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

          {showPettyCash && (
            <button
              type="button"
              className={`btn ${isPettyCashPanelOpen ? 'btn-primary' : 'btn-outline'} flex-align-center justify-content-center gap-2 register-action-btn`}
              onClick={() => setIsPettyCashPanelOpen((open) => !open)}
              disabled={!session}
              title="Registrar y consultar gastos de caja chica del turno"
            >
              <Wallet size={18} /> CAJA CHICA
            </button>
          )}

          {showCloseCashDrawer && (
            <button
              type="button"
              className="btn btn-outline flex-align-center justify-content-center gap-2 register-action-btn"
              onClick={() => setIsCloseCashDrawerOpen(true)}
              disabled={!session}
              title={isDual
                ? `Arqueo y cierre de la sesión de caja (conteo ${primaryCurrencyLabel} opcional)`
                : 'Arqueo y cierre de la sesión de caja'}
            >
              <Lock size={18} /> CERRAR CAJA
            </button>
          )}
        </div>
      </div>

      {/* ── Requisito 2: Aviso de Acceso Limitado (Alineado al Inicio Superior con la Primera Línea del Texto) ── */}
      {!isAdmin && (
        <div 
          className="alert alert-warning mb-4 text-sm p-3 border-warning bg-warning-light d-flex align-start reg-alert-gap"
        >
          <ShieldAlert size={18} className="color-warning flex-shrink-0 reg-alert-icon" />
          <span>Acceso limitado: Los botones <strong>CASH IN</strong> y <strong>CASH OUT</strong> están restringidos a usuarios con rol <strong>Administrador</strong>.</span>
        </div>
      )}

      {loadError && (
        <div className="alert alert-danger mb-4 flex-align-center gap-2" role="alert">
          <AlertCircle size={18} className="flex-shrink-0" />
          <span>{loadError}</span>
        </div>
      )}

      {loading && !session ? (
        <div className="p-5 text-center text-muted card shadow-sm">
          <Loader2 className="animate-spin mb-2 inline-block" size={32} />
          <div>Cargando datos de caja registradora...</div>
        </div>
      ) : (
        <>
          {/* ── T5 (Paso 13 / 8.188): panel de caja chica del turno (paridad WPF) ── */}
          {showPettyCash && isPettyCashPanelOpen && (
            <PettyCashPanel
              expenses={pettyCashExpenses}
              balances={pettyCashBalances}
              canViewBalances={showCloseCashDrawer}
              loading={pettyCashLoading}
              onRegister={() => setIsPettyCashExpenseOpen(true)}
              onCancelExpense={(expense) => setCancelTargetExpense(expense)}
              onRefresh={() => loadPettyCash(session?.id)}
              onClose={() => setIsPettyCashPanelOpen(false)}
            />
          )}

          {/* ── Requisito 3: Tarjetas de Resumen (Espaciado Tipográfico Garantizado entre Número y Divisa Bs.S) ── */}
          <div className="grid w-full gap-4 reg-summary-grid">
            
            {/* Tarjeta 1: EFECTIVO ESPERADO EN CAJA */}
            <div className="card shadow-sm p-4 bg-surface flex-between flex-align-center reg-card-indigo">
              <div className="flex-1 pr-3 flex flex-column justify-content-between">
                <span className="stat-label text-xs font-bold text-muted uppercase tracking-wider block mb-1">
                  EFECTIVO ESPERADO EN CAJA
                </span>
                
                {/* Requisito 3: Espaciado en la divisa (26,673.74 Bs.S) */}
                <div className="stat-val mb-1 flex-align-baseline gap-2 text-nowrap">
                  <span className="text-3xl font-extrabold color-primary">
                    {formatNumberEs(expectedCashBsS, 2)}
                  </span>
                  <span className="text-base font-bold color-primary reg-currency-gap">
                    {localCurrencyLabel}
                  </span>
                </div>

                {isDual && (
                  <div className="text-xs font-semibold text-muted">
                    {formatPrimary(expectedCashUsd)}
                  </div>
                )}
              </div>

              <div className="flex-align-center justify-content-center p-2 rounded-lg flex-shrink-0 reg-card-icon-circle">
                <Landmark size={26} className="color-primary" />
              </div>
            </div>

            {/* Tarjeta 2: TOTAL INGRESOS EN EFECTIVO */}
            <div className="card shadow-sm p-4 bg-surface flex flex-column justify-content-between reg-card-green">
              <div className="flex-align-center gap-2 mb-1">
                <TrendingUp size={18} className="color-success flex-shrink-0" />
                <span className="stat-label text-xs font-bold uppercase tracking-wider reg-label-green">
                  TOTAL INGRESOS EN EFECTIVO
                </span>
              </div>

              {/* Requisito 3: Espaciado en la divisa */}
              <div className="stat-val mb-1 flex-align-baseline gap-2 text-nowrap">
                <span className="text-2xl font-extrabold color-success">
                  {formatNumberEs(totalIncomeBsS, 2)}
                </span>
                <span className="text-sm font-bold color-success reg-currency-gap">
                  {localCurrencyLabel}
                </span>
              </div>

              <div className="text-xs text-muted font-medium">
                Acumulado en sesión
              </div>
            </div>

            {/* Tarjeta 3: TOTAL EGRESOS EN EFECTIVO */}
            <div className="card shadow-sm p-4 bg-surface flex flex-column justify-content-between reg-card-red">
              <div className="flex-align-center gap-2 mb-1">
                <TrendingDown size={18} className="color-danger flex-shrink-0" />
                <span className="stat-label text-xs font-bold uppercase tracking-wider reg-label-red">
                  TOTAL EGRESOS EN EFECTIVO
                </span>
              </div>

              {/* Requisito 3: Espaciado en la divisa */}
              <div className="stat-val mb-1 flex-align-baseline gap-2 text-nowrap">
                <span className="text-2xl font-extrabold color-danger">
                  {formatNumberEs(totalExpenseBsS, 2)}
                </span>
                <span className="text-sm font-bold color-danger reg-currency-gap">
                  {localCurrencyLabel}
                </span>
              </div>

              <div className="text-xs text-muted font-medium">
                Retiros y salidas
              </div>
            </div>

          </div>

          <div className="grid grid-1 gap-4">

            {/* CARRUSEL DE ÚLTIMOS INGRESOS */}
            <div className="card shadow-sm p-4 bg-surface mb-4 w-100 max-w-100 overflow-hidden reg-fullwidth">
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
                  className="recent-incomes-scroll-container flex gap-3.5 py-1 custom-scrollbar w-100 reg-income-scroll"
                >
                  {recentIncomes.map((tx, idx) => {
                    const invoiceTitle = tx.invoiceNumber
                      ? `Factura N° ${tx.invoiceNumber}`
                      : (tx.description || getSourceLabel(tx.source));

                    const amountUsd = tx.amountUsd ?? (tx.exchangeRate > 0 ? tx.amountLocal / tx.exchangeRate : 0);

                    return (
                      <div
                        key={tx.id || idx}
                        className="recent-income-card rounded-lg border flex flex-column justify-content-between shadow-xs flex-shrink-0 p-3 reg-income-card"
                      >
                        <div>
                          <div
                            className="font-bold text-sm text-truncate mb-1.5 text-left w-100"
                            title={invoiceTitle}
                          >
                            {invoiceTitle}
                          </div>

                          <div className="text-xs text-muted mb-3 flex-align-center gap-1.5 w-100 text-left">
                            <Clock size={13} className="flex-shrink-0 reg-clock-mr" />
                            <span>{formatTime(tx.transactionTimeLocal || tx.transactionTime)}</span>
                          </div>
                        </div>

                        <div className="flex-align-baseline gap-2 pt-2 border-top w-100 flex-wrap text-left">
                          <span className="font-extrabold text-sm color-success text-nowrap">
                            {formatSecondary(tx.amountLocal)}
                          </span>
                          {isDual && (
                            <span className="text-xs text-muted font-medium text-nowrap">
                              &nbsp;({formatPrimary(amountUsd)})
                            </span>
                          )}
                        </div>
                      </div>
                    );
                  })}
                </div>
              )}
            </div>

            {/* ── ÁREA 3: TABLA DE MOVIMIENTOS FÍSICOS ── */}
            <div className="card shadow-sm p-4 bg-surface overflow-hidden w-100 max-w-100 reg-fullwidth">
              
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
                  <div className="w-100 overflow-x-auto custom-scrollbar border rounded history-table-wrapper reg-table-scroll">
                    <table className="table table-hover w-100 m-0 cart-table history-main-table reg-table-min">
                      <thead className="sticky-header">
                        <tr className="bg-light">
                          <th className="reg-th-date">FECHA / HORA</th>
                          <th className="reg-th-type">Tipo</th>
                          <th className="reg-th-origin">Origen</th>
                          <th className="reg-th-concept">Concepto / Detalle</th>
                          <th className="text-right reg-th-amount">
                            MONTO ({localCurrencyLabel.toUpperCase()})
                          </th>
                          {isDual && (
                            <th className="text-right reg-th-usd">
                              EQUIV. ({primaryCurrencyLabel})
                            </th>
                          )}
                        </tr>
                      </thead>
                      <tbody>
                        {paginatedTransactions.map((tx, idx) => {
                          const isIncome = tx.type === 0;
                          const amountUsd = tx.amountUsd ?? (tx.exchangeRate > 0 ? tx.amountLocal / tx.exchangeRate : 0);
                          const conceptStr = tx.description || getSourceLabel(tx.source);

                          return (
                            <tr key={tx.id || idx}>
                              {/* Fecha / Hora (Columna Apilada) */}
                              <td className="text-nowrap">
                                <div className="flex-col reg-cell-stack">
                                  <span className="font-semibold text-xs text-primary">
                                    {formatDate(tx.transactionTimeLocal || tx.transactionTime)}
                                  </span>
                                  <span className="text-muted font-mono text-xs">
                                    {formatTime(tx.transactionTimeLocal || tx.transactionTime)}
                                  </span>
                                </div>
                              </td>

                              {/* Tipo Badge */}
                              <td className="reg-td-type">
                                <span 
                                  className={`badge ${isIncome ? 'badge-success' : 'badge-danger'} text-xs font-bold d-inline-flex justify-center text-center reg-badge-type`}
                                >
                                  {isIncome ? 'INGRESO' : 'EGRESO'}
                                </span>
                              </td>

                              {/* Origen */}
                              <td className="text-nowrap">
                                <span className="badge badge-outline text-xs font-mono">
                                  {getSourceLabel(tx.source)}
                                </span>
                              </td>

                              {/* Concepto / Detalle (Truncado sin romper fila) */}
                              <td
                                className="text-sm font-medium text-truncate reg-concept-cell"
                                title={conceptStr}
                              >
                                {conceptStr}
                              </td>

                              {/* Monto local (Alineado a la derecha) */}
                              <td className={`text-right font-extrabold text-sm ${isIncome ? 'color-success' : 'color-danger'} reg-amount-cell`}>
                                {isIncome ? '+' : '-'}{formatSecondary(tx.amountLocal)}
                              </td>

                              {/* Equivalencia primaria (Alineado a la derecha) */}
                              {isDual && (
                                <td className="text-right text-xs text-muted font-medium reg-amount-cell">
                                  {formatPrimary(amountUsd)}
                                </td>
                              )}
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>

                  {/* Pie de Paginación Estandarizado (Igual a Historial de Ventas) */}
                  {filteredTransactions.length > 0 && (
                    <div className="mt-3">
                      <Pagination
                        currentPage={currentPage}
                        totalPages={totalPages}
                        totalCount={filteredTransactions.length}
                        onPageChange={(page) => setCurrentPage(page)}
                        loading={loading}
                        itemLabel="movimientos"
                      />
                    </div>
                  )}
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

      {/* T5 (Paso 13 / 8.188): registro/anulación de caja chica y cierre de la sesión con
          conteo USD opcional. Estas acciones no recalculan políticas: el servidor decide. */}
      {showPettyCash && (
        <PettyCashExpenseModal
          isOpen={isPettyCashExpenseOpen}
          onClose={() => setIsPettyCashExpenseOpen(false)}
          sessionId={session?.id}
          exchangeRate={exchangeRate}
          role={role}
          onSuccess={loadSession}
        />
      )}

      {showPettyCash && (
        <PettyCashCancelModal
          isOpen={Boolean(cancelTargetExpense)}
          onClose={() => setCancelTargetExpense(null)}
          expense={cancelTargetExpense}
          role={role}
          onSuccess={loadSession}
        />
      )}

      {showCloseCashDrawer && (
        <CashDrawerCloseModal
          isOpen={isCloseCashDrawerOpen}
          onClose={() => setIsCloseCashDrawerOpen(false)}
          exchangeRate={exchangeRate}
          expectedCashBsS={expectedCashBsS}
          expectedCashUsd={pettyCashBalances?.usd ?? null}
          role={role}
          onSuccess={loadSession}
        />
      )}

      <CashDrawerOpenModal
        isOpen={isOpenCashDrawerOpen}
        onClose={() => setIsOpenCashDrawerOpen(false)}
        exchangeRate={exchangeRate}
        onSuccess={loadSession}
      />
    </div>
  );
}
