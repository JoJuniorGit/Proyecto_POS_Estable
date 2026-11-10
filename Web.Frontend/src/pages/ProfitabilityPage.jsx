// F4-WU2 (Paso 14): dashboard web de rentabilidad. La vista es de solo lectura sobre el Estado
// de Resultados; las comisiones de medios de pago son costo del negocio y nunca se cargan al
// cliente. Los gráficos usan primitivas nativas SVG/CSS, sin librerías externas.
import { useEffect, useMemo, useState } from 'react';
import {
  AlertTriangle,
  Download,
  Filter,
  Loader2,
  RefreshCw,
  TrendingUp,
} from 'lucide-react';
import RoleGuard from '../navigation/RoleGuard';
import { formatNumberEs } from '../utils/formatters';
import {
  PROFITABILITY_GROUP_BY,
  PROFITABILITY_PRESET,
  PROFITABILITY_SALE_TYPE,
  PROFITABILITY_VIEW_MODE,
  exportProfitability,
  getProfitabilitySummary,
  saveProfitabilityExport,
} from '../services/profitabilityApi';
import './ProfitabilityPage.css';

const PRESET_OPTIONS = Object.freeze([
  { value: PROFITABILITY_PRESET.Today, label: 'Hoy' },
  { value: PROFITABILITY_PRESET.Yesterday, label: 'Ayer' },
  { value: PROFITABILITY_PRESET.Week, label: 'Semana' },
  { value: PROFITABILITY_PRESET.Month, label: 'Mes' },
  { value: PROFITABILITY_PRESET.Custom, label: 'Personalizado' },
]);

const GROUP_BY_OPTIONS = Object.freeze([
  { value: PROFITABILITY_GROUP_BY.Hour, label: 'Hora' },
  { value: PROFITABILITY_GROUP_BY.Day, label: 'Día' },
  { value: PROFITABILITY_GROUP_BY.Week, label: 'Semana' },
  { value: PROFITABILITY_GROUP_BY.Month, label: 'Mes' },
]);

const SALE_TYPE_OPTIONS = Object.freeze([
  { value: '', label: 'Todos' },
  { value: String(PROFITABILITY_SALE_TYPE.Merchandise), label: 'Mercancía' },
  { value: String(PROFITABILITY_SALE_TYPE.CashAdvance), label: 'Avance de efectivo' },
  { value: String(PROFITABILITY_SALE_TYPE.Service), label: 'Servicio' },
]);

const VIEW_MODE_OPTIONS = Object.freeze([
  { value: PROFITABILITY_VIEW_MODE.Accrued, label: 'Devengado' },
  { value: PROFITABILITY_VIEW_MODE.Realized, label: 'Percibido' },
  { value: PROFITABILITY_VIEW_MODE.Both, label: 'Ambos' },
]);

const EXPORT_FORMATS = Object.freeze(['csv', 'xlsx', 'pdf']);

export function formatProfitabilityMoney(summary, value) {
  const symbol = summary?.reportCurrencySymbol || '$';
  return `${symbol} ${formatNumberEs(Number(value) || 0, 2)}`;
}

export function formatProfitabilityPercent(value) {
  return `${formatNumberEs(Number(value) || 0, 2)} %`;
}

export function hasCoverageWarnings(coverage = {}) {
  return Number(coverage.unknownCostRevenue) > 0
    || Number(coverage.unknownCostLinesCount) > 0
    || Number(coverage.unknownFeePaymentAmount) > 0
    || Number(coverage.unknownFeePaymentsCount) > 0;
}

export function isProfitabilityEmpty(summary) {
  if (!summary) return false;
  const breakdown = summary.breakdown || {};
  const collections = [
    breakdown.series,
    breakdown.paymentMethods,
    breakdown.categories,
    breakdown.products,
    breakdown.sellers,
    summary.alerts,
    breakdown.alerts,
  ];
  return collections.every((list) => !Array.isArray(list) || list.length === 0);
}

function todayInputValue() {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
}

function mergeOptions(previous, summary) {
  const mergeList = (current, incoming, idKey, labelKey) => {
    const map = new Map((current || []).map((item) => [String(item.value), item]));
    for (const item of incoming || []) {
      const id = item?.[idKey];
      const label = item?.[labelKey];
      if (id === null || id === undefined || id === 0 || !label) continue;
      map.set(String(id), { value: String(id), label });
    }
    return [...map.values()].sort((left, right) => left.label.localeCompare(right.label, 'es'));
  };

  return {
    categories: mergeList(
      previous?.categories,
      summary?.breakdown?.categories,
      'categoryId',
      'categoryName'
    ),
    products: mergeList(
      previous?.products,
      summary?.breakdown?.products,
      'productId',
      'productName'
    ),
    sellers: mergeList(
      previous?.sellers,
      summary?.breakdown?.sellers,
      'cashierId',
      'cashierName'
    ),
  };
}

export default function ProfitabilityPage() {
  return (
    <RoleGuard
      view="profitability"
      message="La rentabilidad está disponible solo para Administradores y Gerentes."
    >
      <ProfitabilityPageContent />
    </RoleGuard>
  );
}

function ProfitabilityPageContent() {
  const [preset, setPreset] = useState(PROFITABILITY_PRESET.Today);
  const [customFrom, setCustomFrom] = useState(todayInputValue);
  const [customTo, setCustomTo] = useState(todayInputValue);
  const [customApplied, setCustomApplied] = useState(false);
  const [groupBy, setGroupBy] = useState(PROFITABILITY_GROUP_BY.Day);
  const [categoryId, setCategoryId] = useState('');
  const [productId, setProductId] = useState('');
  const [cashierId, setCashierId] = useState('');
  const [saleType, setSaleType] = useState('');
  const [viewMode, setViewMode] = useState(PROFITABILITY_VIEW_MODE.Both);
  const [summary, setSummary] = useState(null);
  const [options, setOptions] = useState({ categories: [], products: [], sellers: [] });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [reloadToken, setReloadToken] = useState(0);
  const [exportingFormat, setExportingFormat] = useState('');
  const [exportStatus, setExportStatus] = useState(null);

  const filters = useMemo(() => {
    const next = { preset, groupBy };

    if (preset === PROFITABILITY_PRESET.Custom) {
      const from = customFrom || todayInputValue();
      const to = customTo || from;
      const ordered = from <= to ? [from, to] : [to, from];
      next.fromDate = ordered[0];
      next.toDate = ordered[1];
    }

    if (categoryId) next.categoryIds = [Number(categoryId)];
    if (productId) next.productIds = [Number(productId)];
    if (cashierId) next.cashierId = Number(cashierId);
    if (saleType !== '') next.saleType = Number(saleType);

    return next;
  }, [preset, groupBy, customFrom, customTo, categoryId, productId, cashierId, saleType]);

  useEffect(() => {
    if (preset === PROFITABILITY_PRESET.Custom && !customApplied) {
      return undefined;
    }

    const controller = new AbortController();
    setLoading(true);
    setError('');

    getProfitabilitySummary(filters, controller.signal)
      .then((data) => {
        if (controller.signal.aborted) return;
        setSummary(data);
        setOptions((previous) => mergeOptions(previous, data));
      })
      .catch((loadError) => {
        if (loadError?.name === 'AbortError') return;
        setSummary(null);
        setError(loadError?.message || 'No se pudo cargar la rentabilidad.');
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });

    return () => controller.abort();
  }, [filters, reloadToken, preset, customApplied]);

  const selectPreset = (value) => {
    if (value === preset && value !== PROFITABILITY_PRESET.Custom) {
      setReloadToken((token) => token + 1);
      return;
    }

    setPreset(value);
    if (value !== PROFITABILITY_PRESET.Custom) {
      setCustomApplied(false);
    }
  };

  const applyCustomRange = () => {
    const from = customFrom || todayInputValue();
    const to = customTo || from;
    if (from > to) {
      setCustomFrom(to);
      setCustomTo(from);
    }
    setPreset(PROFITABILITY_PRESET.Custom);
    setCustomApplied(true);
    setReloadToken((token) => token + 1);
  };

  const clearFilters = () => {
    setCategoryId('');
    setProductId('');
    setCashierId('');
    setSaleType('');
    setGroupBy(PROFITABILITY_GROUP_BY.Day);
  };

  const refresh = () => setReloadToken((token) => token + 1);

  const canExport = Boolean(summary)
    && !loading
    && !exportingFormat
    && !(preset === PROFITABILITY_PRESET.Custom && !customApplied);

  const handleExport = async (format) => {
    if (!canExport) return;

    setExportingFormat(format);
    setExportStatus(null);
    try {
      const file = await exportProfitability(filters, format);
      const saved = saveProfitabilityExport(file);
      setExportStatus({
        type: 'success',
        text: saved
          ? `Reporte ${format.toUpperCase()} exportado correctamente.`
          : `Reporte ${format.toUpperCase()} generado. La descarga no está disponible en este entorno.`,
      });
    } catch (exportError) {
      setExportStatus({
        type: 'danger',
        text: exportError?.message || `No se pudo exportar el reporte ${format.toUpperCase()}.`,
      });
    } finally {
      setExportingFormat('');
    }
  };

  return (
    <div className="profitability-page">
      <header className="profitability-header">
        <div className="profitability-title-block">
          <h2 className="page-title profitability-title">
            <TrendingUp size={24} className="color-primary flex-shrink-0" />
            <span>Rentabilidad</span>
          </h2>
          <p className="text-muted profitability-subtitle">
            Estado de Resultados devengado y percibido. Las comisiones de medios de pago son
            costo del negocio y nunca se cargan al cliente.
          </p>
        </div>

        <div className="profitability-header-actions">
          <button
            type="button"
            className="btn btn-outline btn-sm"
            onClick={refresh}
            disabled={loading}
            aria-label="Actualizar rentabilidad"
          >
            <RefreshCw size={15} className={loading ? 'animate-spin' : ''} /> Actualizar
          </button>

          {EXPORT_FORMATS.map((format) => (
            <button
              key={format}
              type="button"
              className="btn btn-outline btn-sm"
              onClick={() => handleExport(format)}
              disabled={!canExport}
              aria-label={`Exportar ${format.toUpperCase()}`}
            >
              {exportingFormat === format
                ? <Loader2 size={15} className="animate-spin" />
                : <Download size={15} />}
              {format.toUpperCase()}
            </button>
          ))}
        </div>
      </header>

      <section className="card profitability-filters" aria-label="Filtros de rentabilidad">
        <div className="profitability-filter-row">
          <div className="profitability-preset-group" role="group" aria-label="Período">
            <span className="profitability-filter-label">Período:</span>
            {PRESET_OPTIONS.map((option) => (
              <button
                key={option.label}
                type="button"
                className={`btn btn-sm ${preset === option.value ? 'btn-primary' : 'btn-outline'}`}
                aria-pressed={preset === option.value}
                onClick={() => selectPreset(option.value)}
              >
                {option.label}
              </button>
            ))}
          </div>

          {preset === PROFITABILITY_PRESET.Custom && (
            <div className="profitability-custom-range">
              <label className="profitability-filter-field">
                <span className="profitability-filter-label">Desde</span>
                <input
                  type="date"
                  className="form-input"
                  value={customFrom}
                  onChange={(event) => setCustomFrom(event.target.value)}
                />
              </label>
              <label className="profitability-filter-field">
                <span className="profitability-filter-label">Hasta</span>
                <input
                  type="date"
                  className="form-input"
                  value={customTo}
                  onChange={(event) => setCustomTo(event.target.value)}
                />
              </label>
              <button type="button" className="btn btn-primary btn-sm" onClick={applyCustomRange}>
                Aplicar
              </button>
            </div>
          )}
        </div>

        <div className="profitability-filter-row profitability-filter-row-secondary">
          <label className="profitability-filter-field">
            <span className="profitability-filter-label">Categoría</span>
            <select
              className="form-input"
              value={categoryId}
              onChange={(event) => setCategoryId(event.target.value)}
            >
              <option value="">Todas</option>
              {options.categories.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </select>
          </label>

          <label className="profitability-filter-field">
            <span className="profitability-filter-label">Producto</span>
            <select
              className="form-input"
              value={productId}
              onChange={(event) => setProductId(event.target.value)}
            >
              <option value="">Todos</option>
              {options.products.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </select>
          </label>

          <label className="profitability-filter-field">
            <span className="profitability-filter-label">Cajero</span>
            <select
              className="form-input"
              value={cashierId}
              onChange={(event) => setCashierId(event.target.value)}
            >
              <option value="">Todos</option>
              {options.sellers.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </select>
          </label>

          <label className="profitability-filter-field">
            <span className="profitability-filter-label">Tipo de venta</span>
            <select
              className="form-input"
              value={saleType}
              onChange={(event) => setSaleType(event.target.value)}
            >
              {SALE_TYPE_OPTIONS.map((option) => (
                <option key={option.label} value={option.value}>{option.label}</option>
              ))}
            </select>
          </label>

          <label className="profitability-filter-field">
            <span className="profitability-filter-label">Agrupar por</span>
            <select
              className="form-input"
              value={groupBy}
              onChange={(event) => setGroupBy(Number(event.target.value))}
            >
              {GROUP_BY_OPTIONS.map((option) => (
                <option key={option.label} value={option.value}>{option.label}</option>
              ))}
            </select>
          </label>

          <button type="button" className="btn btn-outline btn-sm" onClick={clearFilters}>
            <Filter size={15} /> Limpiar filtros
          </button>
        </div>
      </section>

      {exportStatus && (
        <div className={`alert alert-${exportStatus.type} profitability-export-status`}>
          {exportStatus.text}
        </div>
      )}

      <ProfitabilityDashboard
        summary={summary}
        viewMode={viewMode}
        onViewModeChange={setViewMode}
        isLoading={loading}
        error={error}
      />
    </div>
  );
}

export function ProfitabilityDashboard({
  summary = null,
  viewMode = PROFITABILITY_VIEW_MODE.Both,
  onViewModeChange,
  isLoading = false,
  error = '',
}) {
  const accrued = summary?.accrued || {};
  const realized = summary?.realized || {};
  const coverage = summary?.coverage || {};
  const breakdown = summary?.breakdown || {};
  const alerts = Array.isArray(summary?.alerts) && summary.alerts.length > 0
    ? summary.alerts
    : (breakdown.alerts || []);
  const empty = isProfitabilityEmpty(summary);
  const money = (value) => formatProfitabilityMoney(summary, value);
  const percent = (value) => formatProfitabilityPercent(value);

  return (
    <div className="profitability-dashboard" data-testid="profitability-dashboard">
      {error && <div className="alert alert-danger profitability-error">{error}</div>}

      {isLoading && (
        <div className="profitability-loading" role="status">
          <Loader2 size={18} className="animate-spin" /> Cargando rentabilidad...
        </div>
      )}

      <section className="profitability-kpis-headline" aria-label="Resultados principales">
        <KpiCard
          variant="headline"
          testId="kpi-utilidad-devengada"
          label="UTILIDAD DEVENGADA"
          value={money(accrued.netProfit)}
          hint="Resultado neto del período por ventas y gastos devengados"
        />
        <KpiCard
          variant="secondary"
          testId="kpi-utilidad-percibida"
          label="UTILIDAD PERCIBIDA EN CAJA"
          value={money(realized.netProfit)}
          hint="Resultado neto de los cobros efectivamente realizados"
        />
      </section>

      <section className="profitability-kpis" aria-label="Indicadores del período">
        <KpiCard label="VENTAS NETAS" value={money(accrued.netSales)} />
        <KpiCard label="CMV" value={money(accrued.cogs)} />
        <KpiCard label="UTILIDAD BRUTA" value={money(accrued.grossProfit)} />
        <KpiCard label="MARGEN BRUTO" value={percent(accrued.grossMarginPercent)} />
        <KpiCard label="COMISIONES DE SERVICIO" value={money(accrued.serviceCommissions)} />
        <KpiCard label="DIFERENCIA FX (INFORMATIVA)" value={money(accrued.fxDifferenceInformational)} />
        <KpiCard label="CAJA CHICA" value={money(accrued.pettyCashExpenses)} />
        <KpiCard label="COMISIONES DE MEDIOS DE PAGO" value={money(accrued.paymentMethodFees)} />
        <KpiCard label="UTILIDAD NETA" value={money(accrued.netProfit)} />
        <KpiCard label="MARGEN NETO" value={percent(accrued.netMarginPercent)} />
      </section>

      {summary && (
        <CoverageBanner
          coverage={coverage}
          summary={summary}
          globalCostsNote={summary.globalCostsNote}
        />
      )}

      {summary && (
        <ViewModeToggle viewMode={viewMode} onChange={onViewModeChange} />
      )}

      {summary && !empty && (
        <ProfitabilitySeriesChart points={breakdown.series || []} viewMode={viewMode} />
      )}

      {summary && !empty && (
        <SeriesTable
          points={breakdown.series || []}
          viewMode={viewMode}
          money={money}
        />
      )}

      {summary && !empty && (
        <div className="profitability-grid">
          <PaymentMethodsTable rows={breakdown.paymentMethods || []} money={money} percent={percent} />
          <ProductsTable rows={breakdown.products || []} money={money} percent={percent} />
          <CategoriesTable rows={breakdown.categories || []} money={money} percent={percent} />
          <SellersTable rows={breakdown.sellers || []} money={money} />
        </div>
      )}

      {summary && !empty && <AlertsPanel alerts={alerts} percent={percent} />}

      {summary && empty && (
        <div className="profitability-empty" data-testid="profitability-empty">
          <TrendingUp size={42} className="text-muted" />
          <p className="font-bold">Sin movimientos para el período seleccionado</p>
          <p className="text-muted">
            Ajuste el rango de fechas o los filtros para ver el Estado de Resultados.
          </p>
        </div>
      )}
    </div>
  );
}

function KpiCard({ label, value, hint, variant = 'default', testId }) {
  return (
    <div
      className={`profitability-kpi profitability-kpi-${variant}`}
      data-testid={testId}
    >
      <span className="profitability-kpi-label">{label}</span>
      <strong className="profitability-kpi-value">{value}</strong>
      {hint && <span className="profitability-kpi-hint">{hint}</span>}
    </div>
  );
}

function CoverageBanner({ coverage, summary, globalCostsNote }) {
  const warning = hasCoverageWarnings(coverage);
  return (
    <section
      className={`profitability-coverage${warning ? ' is-warning' : ''}`}
      data-testid="coverage-banner"
    >
      <h3 className="profitability-coverage-title">
        <AlertTriangle size={16} /> Cobertura de datos históricos
      </h3>
      <div className="profitability-coverage-items">
        <span><strong>Costo:</strong> {formatProfitabilityPercent(coverage.costCoveragePercent)}</span>
        <span><strong>Comisiones:</strong> {formatProfitabilityPercent(coverage.feeCoveragePercent)}</span>
        <span>
          <strong>Ingreso sin costo:</strong> {formatProfitabilityMoney(summary, coverage.unknownCostRevenue)}
          {' '}{`(${coverage.unknownCostLinesCount || 0} líneas)`}
        </span>
        <span>
          <strong>Cobros sin fee:</strong> {formatProfitabilityMoney(summary, coverage.unknownFeePaymentAmount)}
          {' '}{`(${coverage.unknownFeePaymentsCount || 0} pagos)`}
        </span>
      </div>
      <p className="profitability-coverage-note">
        Los valores desconocidos se informan sin inventar ni reprocesar datos históricos.
      </p>
      {globalCostsNote && (
        <p className="profitability-coverage-note profitability-coverage-global">
          {globalCostsNote}
        </p>
      )}
    </section>
  );
}

function ViewModeToggle({ viewMode, onChange }) {
  return (
    <div className="profitability-view-toggle" role="group" aria-label="Vista de rentabilidad">
      <span className="profitability-filter-label">Vista:</span>
      {VIEW_MODE_OPTIONS.map((option) => (
        <button
          key={option.value}
          type="button"
          aria-pressed={viewMode === option.value}
          className={`btn btn-sm ${viewMode === option.value ? 'btn-primary' : 'btn-outline'}`}
          onClick={() => onChange?.(option.value)}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}

export function ProfitabilitySeriesChart({ points = [], viewMode = PROFITABILITY_VIEW_MODE.Both }) {
  const safePoints = Array.isArray(points) ? points : [];
  const showAccrued = viewMode !== PROFITABILITY_VIEW_MODE.Realized;
  const showRealized = viewMode !== PROFITABILITY_VIEW_MODE.Accrued;
  const max = Math.max(
    1,
    ...safePoints.map((point) => Math.max(
      showAccrued ? Math.abs(Number(point.accruedNetProfit) || 0) : 0,
      showRealized ? Math.abs(Number(point.realizedNetProfit) || 0) : 0
    ))
  );

  const width = 720;
  const height = 240;
  const padding = { top: 20, right: 16, bottom: 38, left: 16 };
  const plotWidth = width - padding.left - padding.right;
  const plotHeight = height - padding.top - padding.bottom;
  const zeroY = padding.top + plotHeight / 2;
  const slot = safePoints.length > 0 ? plotWidth / safePoints.length : plotWidth;
  const barWidth = Math.max(3, Math.min(20, slot / (showAccrued && showRealized ? 3 : 2)));
  const labelStep = safePoints.length > 12 ? Math.ceil(safePoints.length / 12) : 1;

  return (
    <section className="card profitability-chart-card">
      <h3 className="profitability-section-title">Serie temporal: utilidad neta</h3>
      <p className="text-muted profitability-section-subtitle">
        Devengado vs. percibido por período agrupado
      </p>
      <div className="profitability-chart-scroll">
        <svg
          data-testid="profitability-series-chart"
          role="img"
          aria-label="Serie temporal de utilidad neta"
          viewBox={`0 0 ${width} ${height}`}
          className="profitability-series-svg"
        >
          <line
            x1={padding.left}
            y1={zeroY}
            x2={width - padding.right}
            y2={zeroY}
            className="profitability-zero-line"
          />
          {safePoints.map((point, index) => {
            const center = padding.left + slot * index + slot / 2;
            const accrued = Number(point.accruedNetProfit) || 0;
            const realized = Number(point.realizedNetProfit) || 0;
            const accruedHeight = (Math.abs(accrued) / max) * (plotHeight / 2);
            const realizedHeight = (Math.abs(realized) / max) * (plotHeight / 2);
            const accruedX = showAccrued && showRealized
              ? center - barWidth - 1
              : center - barWidth / 2;
            const realizedX = showAccrued && showRealized
              ? center + 1
              : center - barWidth / 2;

            return (
              <g key={`${point.label || 'period'}-${index}`}>
                {showAccrued && (
                  <rect
                    data-series="accrued"
                    x={accruedX}
                    y={accrued >= 0 ? zeroY - accruedHeight : zeroY}
                    width={barWidth}
                    height={Math.max(1, accruedHeight)}
                    rx="2"
                    className={accrued < 0 ? 'profitability-bar-negative' : 'profitability-bar-accrued'}
                  />
                )}
                {showRealized && (
                  <rect
                    data-series="realized"
                    x={realizedX}
                    y={realized >= 0 ? zeroY - realizedHeight : zeroY}
                    width={barWidth}
                    height={Math.max(1, realizedHeight)}
                    rx="2"
                    className={realized < 0 ? 'profitability-bar-negative' : 'profitability-bar-realized'}
                  />
                )}
                {index % labelStep === 0 && (
                  <text
                    x={center}
                    y={height - 12}
                    textAnchor="middle"
                    className="profitability-axis-label"
                  >
                    {point.label}
                  </text>
                )}
              </g>
            );
          })}
        </svg>
      </div>
    </section>
  );
}

function SeriesTable({ points, viewMode, money }) {
  const showAccrued = viewMode !== PROFITABILITY_VIEW_MODE.Realized;
  const showRealized = viewMode !== PROFITABILITY_VIEW_MODE.Accrued;

  return (
    <section className="card profitability-table-card">
      <h3 className="profitability-section-title">Serie temporal</h3>
      <div className="overflow-x-auto">
        <table className="cart-table" data-testid="series-table">
          <thead>
            <tr>
              <th>Período</th>
              {showAccrued && <th>Utilidad devengada</th>}
              {showAccrued && <th>Ventas netas</th>}
              {showRealized && <th>Utilidad percibida</th>}
              {showRealized && <th>Cobros</th>}
            </tr>
          </thead>
          <tbody>
            {points.map((point, index) => (
              <tr key={`${point.label || 'period'}-${index}`}>
                <td className="font-medium">{point.label}</td>
                {showAccrued && <td className="font-mono">{money(point.accruedNetProfit)}</td>}
                {showAccrued && <td className="font-mono">{money(point.accruedNetSales)}</td>}
                {showRealized && <td className="font-mono">{money(point.realizedNetProfit)}</td>}
                {showRealized && <td className="font-mono">{money(point.realizedCollectedRevenue)}</td>}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function PaymentMethodsTable({ rows, money, percent }) {
  const max = Math.max(
    1,
    ...rows.map((row) => Math.abs(Number(row.collectedAmount) || 0))
  );

  return (
    <section className="card profitability-table-card">
      <h3 className="profitability-section-title">Métodos de pago</h3>
      <p className="text-muted profitability-section-subtitle">
        Cobros y comisión neta del período
      </p>
      <div className="overflow-x-auto">
        <table className="cart-table">
          <thead>
            <tr>
              <th>Método</th>
              <th>Cobros</th>
              <th>Comisión</th>
              <th>Cobertura fee</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => {
              const ratio = Math.max(
                2,
                (Math.abs(Number(row.collectedAmount) || 0) / max) * 100
              );
              return (
                <tr key={row.paymentMethodId}>
                  <td className="font-medium">{row.paymentMethodName}</td>
                  <td>
                    <div className="profitability-bar-cell">
                      <span className="profitability-bar-track">
                        <span
                          data-testid="payment-method-bar"
                          className="profitability-bar profitability-bar-primary"
                          style={{ width: `${ratio}%` }}
                        />
                      </span>
                      <span className="font-mono">{money(row.collectedAmount)}</span>
                    </div>
                  </td>
                  <td className="font-mono">{money(row.feeAmount)}</td>
                  <td>{percent(row.feeCoveragePercent)}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function ProductsTable({ rows, money, percent }) {
  const max = Math.max(1, ...rows.map((row) => Math.abs(Number(row.revenue) || 0)));

  return (
    <section className="card profitability-table-card">
      <h3 className="profitability-section-title">Productos</h3>
      <p className="text-muted profitability-section-subtitle">
        Ranking por ingreso del período
      </p>
      <div className="overflow-x-auto">
        <table className="cart-table">
          <thead>
            <tr>
              <th>Producto</th>
              <th>Categoría</th>
              <th>Cantidad</th>
              <th>Ingreso</th>
              <th>Utilidad</th>
              <th>Margen</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr key={row.productId} data-testid="product-row">
                <td className="font-medium">{row.productName}</td>
                <td>{row.categoryName || 'Sin categoría'}</td>
                <td className="text-right font-mono">{formatNumberEs(row.quantitySold, 2)}</td>
                <td>
                  <div className="profitability-bar-cell">
                    <span className="profitability-bar-track">
                      <span
                        className="profitability-bar profitability-bar-success"
                        style={{
                          width: `${Math.max(2, (Math.abs(Number(row.revenue) || 0) / max) * 100)}%`,
                        }}
                      />
                    </span>
                    <span className="font-mono">{money(row.revenue)}</span>
                  </div>
                </td>
                <td className="font-mono">{money(row.grossProfit)}</td>
                <td>{percent(row.grossMarginPercent)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function CategoriesTable({ rows, money, percent }) {
  return (
    <section className="card profitability-table-card">
      <h3 className="profitability-section-title">Categorías</h3>
      <div className="overflow-x-auto">
        <table className="cart-table">
          <thead>
            <tr>
              <th>Categoría</th>
              <th>Ingreso</th>
              <th>Utilidad</th>
              <th>Margen</th>
              <th>Contribución</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr key={row.categoryId}>
                <td className="font-medium">{row.categoryName}</td>
                <td className="font-mono">{money(row.revenue)}</td>
                <td className="font-mono">{money(row.grossProfit)}</td>
                <td>{percent(row.grossMarginPercent)}</td>
                <td>{percent(row.contributionPercent)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function SellersTable({ rows, money }) {
  return (
    <section className="card profitability-table-card">
      <h3 className="profitability-section-title">Vendedores / cajeros</h3>
      <div className="overflow-x-auto">
        <table className="cart-table">
          <thead>
            <tr>
              <th>Cajero</th>
              <th>Ventas netas</th>
              <th>Utilidad bruta</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr key={row.cashierId ?? row.cashierName}>
                <td className="font-medium">{row.cashierName || 'Sin cajero'}</td>
                <td className="font-mono">{money(row.netSales)}</td>
                <td className="font-mono">{money(row.grossProfit)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function AlertsPanel({ alerts, percent }) {
  const severityLabel = (severity) => {
    if (Number(severity) === 2) return 'Crítico';
    if (Number(severity) === 1) return 'Advertencia';
    return 'Información';
  };

  const severityClass = (severity) => {
    if (Number(severity) === 2) return 'critical';
    if (Number(severity) === 1) return 'warning';
    return 'info';
  };

  return (
    <section className="card profitability-alerts">
      <h3 className="profitability-section-title">Alertas de erosión de margen</h3>
      {alerts.length === 0 ? (
        <p className="text-muted">Sin alertas de erosión de margen en el período.</p>
      ) : (
        <div className="overflow-x-auto">
          <table className="cart-table" data-testid="alerts-table">
            <thead>
              <tr>
                <th>Severidad</th>
                <th>Alerta</th>
                <th>Actual</th>
                <th>Anterior</th>
                <th>Variación</th>
              </tr>
            </thead>
            <tbody>
              {alerts.map((alert, index) => (
                <tr
                  key={`${alert.code || 'alert'}-${index}`}
                  className={`profitability-alert-row profitability-alert-${severityClass(alert.severity)}`}
                >
                  <td className="profitability-alert-severity">{severityLabel(alert.severity)}</td>
                  <td className="profitability-alert-message">{alert.message}</td>
                  <td className="font-mono">{percent(alert.currentValue)}</td>
                  <td className="font-mono">{percent(alert.previousValue)}</td>
                  <td className="font-mono">{percent(alert.delta)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
