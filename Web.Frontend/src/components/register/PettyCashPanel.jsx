import { useMemo, useState } from 'react';
import {
  HandCoins,
  Plus,
  RefreshCw,
  X,
  FileText,
  CircleX,
  Loader2,
  Search,
} from 'lucide-react';
import { useMonetaryProfile } from '../../context/MonetaryProfileContext';
import { displayCurrencyCode } from '../../utils/monetaryProfile';
import {
  PettyCashCurrency,
  PettyCashStatus,
  getPettyCashCategoryLabel,
  getPettyCashStatusLabel,
  getPettyCashVoucher,
} from '../../services/pettyCashApi';
import {
  getPettyCashAuthorizerDisplay,
  getPettyCashCreatedAtDisplay,
  getPettyCashVoucherCode,
  getPettyCashVoucherFileName,
  profileIncludesCurrency,
  resolveLocalCurrencyProfile,
  summarizePettyCashExpenses,
} from './registerFormLogic';
import './PettyCashPanel.css';

/**
 * T5 (Paso 13 / 8.188, S1/S2/S5/S7): panel de caja chica del turno (paridad con el panel WPF).
 * Presenta los snapshots congelados que devuelve el servidor sin recalcular montos: categoría,
 * concepto/beneficiario, montos duales, estado, autorizador y vale. Los saldos físicos por gaveta
 * se muestran solo a supervisión (Admin/Manager); el Cashier conserva el arqueo ciego.
 */
export default function PettyCashPanel({
  expenses = [],
  balances = null,
  canViewBalances = false,
  loading = false,
  onRegister,
  onCancelExpense,
  onRefresh,
  onClose,
  onDownloadVoucher,
  dedicated = false,
}) {
  const {
    profile,
    formatPrimary,
    formatSecondary,
  } = useMonetaryProfile();
  const { isDual, label: localCurrencyLabel } = resolveLocalCurrencyProfile(profile);
  const usdCurrencyLabel = displayCurrencyCode('USD');
  const showUsd = isDual && profileIncludesCurrency(profile, 'USD');
  const [voucherError, setVoucherError] = useState('');
  const [downloadingId, setDownloadingId] = useState(null);
  const [statusFilter, setStatusFilter] = useState('all');
  const [search, setSearch] = useState('');

  const summary = summarizePettyCashExpenses(expenses);
  const filteredExpenses = useMemo(() => {
    const term = search.trim().toLowerCase();
    return expenses.filter((expense) => {
      const isCancelled = Number(expense.status) === PettyCashStatus.Cancelled;
      if (statusFilter === 'active' && isCancelled) return false;
      if (statusFilter === 'cancelled' && !isCancelled) return false;
      if (!term) return true;

      const haystack = [
        getPettyCashVoucherCode(expense),
        expense.concept,
        expense.beneficiary,
        expense.referenceNumber,
        getPettyCashCategoryLabel(expense.category),
      ]
        .filter(Boolean)
        .join(' ')
        .toLowerCase();
      return haystack.includes(term);
    });
  }, [expenses, search, statusFilter]);
  const balanceBsSDisplay = canViewBalances && balances ? formatSecondary(balances.bsS) : '—';
  const balanceUsdDisplay = canViewBalances && balances ? formatPrimary(balances.usd) : '—';
  const expenseCurrencyLabel = (currency) => (
    Number(currency) === PettyCashCurrency.Usd ? usdCurrencyLabel : localCurrencyLabel
  );

  async function handleDownloadVoucher(expense) {
    setVoucherError('');
    if (typeof onDownloadVoucher === 'function') {
      onDownloadVoucher(expense);
      return;
    }

    setDownloadingId(expense.id);
    try {
      const blob = await getPettyCashVoucher(expense.id);
      if (typeof document === 'undefined' || typeof URL === 'undefined' || typeof URL.createObjectURL !== 'function') {
        return;
      }
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = getPettyCashVoucherFileName(expense);
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      URL.revokeObjectURL(url);
    } catch (err) {
      console.error('[PettyCashPanel] No se pudo descargar el vale:', err);
      setVoucherError(err?.message || 'No se pudo generar el vale de caja chica.');
    } finally {
      setDownloadingId(null);
    }
  }

  return (
    <section className={`card shadow-sm p-4 bg-surface mb-4 pc-panel${dedicated ? ' pc-panel-dedicated' : ''}`}>
      <div className="pc-panel-header">
        <div className="pc-title-group">
          <h3 className="pc-title flex-align-center gap-2 m-0 text-base font-bold">
            <HandCoins size={20} className="pc-title-icon" />
            <span>CAJA CHICA DEL TURNO</span>
          </h3>
          <span className="pc-summary text-xs text-muted">
            {`${summary.activeCount} activo(s) · ${formatSecondary(summary.totalBsS)}${showUsd ? ` · ${formatPrimary(summary.totalUsd)}` : ''}`}
          </span>
        </div>

        <div className="pc-actions">
          <button type="button" className="btn btn-outline pc-action-register flex-align-center gap-2" onClick={onRegister}>
            <Plus size={16} /> REGISTRAR GASTO
          </button>
          <button
            type="button"
            className="btn btn-outline pc-icon-btn"
            onClick={onRefresh}
            disabled={loading}
            title="Refrescar gastos de caja chica"
          >
            <RefreshCw size={16} className={loading ? 'animate-spin' : ''} />
          </button>
          <button
            type="button"
            className="btn btn-outline pc-icon-btn"
            onClick={onClose}
            title={dedicated ? 'Volver a movimientos de caja' : 'Ocultar panel de caja chica'}
          >
            <X size={16} />
          </button>
        </div>
      </div>

      <div className="pc-kpis">
        <div className="pc-kpi">
          <span className="pc-kpi-label">GASTOS ACTIVOS</span>
          <span className="pc-kpi-value">{summary.activeCount}</span>
          <span className="pc-kpi-hint">del turno</span>
        </div>
        <div className="pc-kpi">
          <span className="pc-kpi-label">TOTAL ACTIVO {localCurrencyLabel.toUpperCase()}</span>
          <span className="pc-kpi-value pc-kpi-money">{formatSecondary(summary.totalBsS)}</span>
          <span className="pc-kpi-hint">solo gastos activos</span>
        </div>
        {showUsd && (
          <div className="pc-kpi">
            <span className="pc-kpi-label">TOTAL ACTIVO {usdCurrencyLabel}</span>
            <span className="pc-kpi-value pc-kpi-money">{formatPrimary(summary.totalUsd)}</span>
            <span className="pc-kpi-hint">equivalente del turno</span>
          </div>
        )}
        <div className="pc-kpi pc-kpi-muted">
          <span className="pc-kpi-label">ANULADOS</span>
          <span className="pc-kpi-value">{summary.cancelledCount}</span>
          <span className="pc-kpi-hint">no afectan el saldo</span>
        </div>
      </div>

      <div className="pc-toolbar">
        <div className="pc-filter-chips" role="group" aria-label="Filtrar gastos de caja chica">
          <button
            type="button"
            className={`pc-filter-chip${statusFilter === 'all' ? ' is-active' : ''}`}
            onClick={() => setStatusFilter('all')}
          >
            Todos <span>{summary.totalCount}</span>
          </button>
          <button
            type="button"
            className={`pc-filter-chip${statusFilter === 'active' ? ' is-active' : ''}`}
            onClick={() => setStatusFilter('active')}
          >
            Activos <span>{summary.activeCount}</span>
          </button>
          <button
            type="button"
            className={`pc-filter-chip${statusFilter === 'cancelled' ? ' is-active' : ''}`}
            onClick={() => setStatusFilter('cancelled')}
          >
            Anulados <span>{summary.cancelledCount}</span>
          </button>
        </div>
        <label className="pc-search">
          <Search size={15} className="pc-search-icon" />
          <input
            type="search"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder="Buscar vale, concepto o beneficiario..."
            aria-label="Buscar gasto de caja chica"
          />
        </label>
      </div>

      <div className="pc-balances">
        <span className="pc-balance-item">
          <span className="text-muted text-xs font-semibold">Efectivo físico {localCurrencyLabel}:</span>
          <span className="pc-balance-value">{balanceBsSDisplay}</span>
        </span>
        {showUsd && (
          <span className="pc-balance-item">
            <span className="text-muted text-xs font-semibold">Efectivo físico {usdCurrencyLabel}:</span>
            <span className="pc-balance-value">{balanceUsdDisplay}</span>
          </span>
        )}
        {loading && (
          <span className="pc-loading text-xs text-muted flex-align-center gap-1">
            <Loader2 size={14} className="animate-spin" /> Cargando...
          </span>
        )}
      </div>

      {voucherError && (
        <div className="alert alert-danger text-xs p-2 mb-2">{voucherError}</div>
      )}

      {filteredExpenses.length === 0 ? (
        <div className="p-4 text-center text-muted border border-dashed rounded">
          {expenses.length === 0
            ? 'No hay gastos de caja chica registrados en este turno.'
            : 'No hay gastos que coincidan con el filtro o la búsqueda.'}
        </div>
      ) : (
        <div className="pc-table-wrapper custom-scrollbar">
          <table className="pc-table">
            <thead>
              <tr>
                <th>VALE</th>
                <th>FECHA / HORA</th>
                <th>CATEGORÍA</th>
                <th className="pc-th-concept">CONCEPTO / BENEFICIARIO</th>
                <th className="text-right">MONTO {localCurrencyLabel.toUpperCase()}</th>
                {showUsd && <th className="text-right">MONTO {usdCurrencyLabel}</th>}
                <th>ESTADO</th>
                <th>AUTORIZADOR</th>
                <th className="text-center">ACCIONES</th>
              </tr>
            </thead>
            <tbody>
              {filteredExpenses.map((expense) => {
                const isCancelled = Number(expense.status) === PettyCashStatus.Cancelled;

                return (
                  <tr key={expense.id} className={isCancelled ? 'pc-row-cancelled' : ''}>
                    <td className="pc-td-voucher font-mono font-bold">
                      {getPettyCashVoucherCode(expense)}
                    </td>
                    <td className="text-nowrap text-xs">
                      {getPettyCashCreatedAtDisplay(expense.createdAtUtc)}
                    </td>
                    <td className="text-xs">{getPettyCashCategoryLabel(expense.category)}</td>
                    <td className="pc-td-concept">
                      <span className="pc-concept font-semibold text-sm">{expense.concept}</span>
                      <span className="pc-concept-beneficiary">{expense.beneficiary}</span>
                      <span className="pc-concept-reference text-muted">
                        {expenseCurrencyLabel(expense.currency)} · Ref: {expense.referenceNumber || '—'}
                      </span>
                      {isCancelled && expense.cancellationReason && (
                        <span className="pc-concept-reference pc-cancellation-reason">
                          Motivo de anulación: {expense.cancellationReason}
                        </span>
                      )}
                    </td>
                    <td className="text-right font-mono font-bold text-sm">{formatSecondary(expense.amountBsS)}</td>
                    {showUsd && <td className="text-right font-mono text-sm text-muted">{formatPrimary(expense.amountUsd)}</td>}
                    <td>
                      <span className={isCancelled ? 'pc-status-cancelled' : 'pc-status-active'}>
                        {getPettyCashStatusLabel(expense.status)}
                      </span>
                    </td>
                    <td className="text-xs">{getPettyCashAuthorizerDisplay(expense)}</td>
                    <td>
                      <div className="pc-row-actions">
                        <button
                          type="button"
                          className="btn btn-outline pc-icon-btn"
                          onClick={() => handleDownloadVoucher(expense)}
                          disabled={downloadingId === expense.id}
                          aria-label="Descargar vale"
                        >
                          {downloadingId === expense.id
                            ? <Loader2 size={15} className="animate-spin" />
                            : <FileText size={15} />}
                        </button>
                        {!isCancelled && (
                          <button
                            type="button"
                            className="btn btn-outline pc-icon-btn pc-icon-btn-danger"
                            onClick={() => onCancelExpense?.(expense)}
                            aria-label="Anular gasto (solo activos)"
                          >
                            <CircleX size={15} />
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
