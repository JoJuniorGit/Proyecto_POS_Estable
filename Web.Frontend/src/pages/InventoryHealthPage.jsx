import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  AlertTriangle,
  BarChart3,
  ChevronLeft,
  ChevronRight,
  Loader2,
  Percent,
  RefreshCw,
  Tag,
  X,
} from 'lucide-react';
import RoleGuard from '../navigation/RoleGuard';
import useDebounce from '../hooks/useDebounce';
import { getCategories, toCategoryOptions } from '../services/categoriesApi';
import {
  applyDeadStockPromotion,
  getDeadStockSelection,
  getInventoryHealthReport,
} from '../services/inventoryHealthApi';
import { formatNumberEs } from '../utils/formatters';
import './InventoryHealthPage.css';

const DEAD_STOCK_OPTIONS = Object.freeze([45, 60, 90]);
const SALES_WINDOW_OPTIONS = Object.freeze([30, 60, 90, 180]);
const ROTATION_CLASS_OPTIONS = Object.freeze([
  { value: '', label: 'Todas las rotaciones' },
  { value: 'High', label: 'Alta rotacion' },
  { value: 'Medium', label: 'Media rotacion' },
  { value: 'Low', label: 'Baja rotacion' },
  { value: 'DeadStock', label: 'Stock muerto' },
]);

export function formatHealthMoney(value) {
  return `$ ${formatNumberEs(Number(value) || 0, 2)}`;
}

export function formatHealthPercent(value) {
  return `${formatNumberEs(Number(value) || 0, 1)} %`;
}

export function clampHealthPercent(value) {
  const number = Number(value) || 0;
  return Math.min(100, Math.max(0, number));
}

export function inventoryHealthEmpty(report) {
  return !report || !Array.isArray(report.products) || report.products.length === 0;
}

export function rotationBadgeClass(rotationClass) {
  switch (rotationClass) {
    case 'High':
      return 'ih-badge ih-badge-high';
    case 'Medium':
      return 'ih-badge ih-badge-medium';
    case 'DeadStock':
      return 'ih-badge ih-badge-dead';
    default:
      return 'ih-badge ih-badge-low';
  }
}

export function InventoryHealthDashboard({
  report,
  loading = false,
  error = '',
  filters,
  onFilterChange,
  onRefresh,
  onOpenPromotion,
  categories = [],
  onPreviousPage,
  onNextPage,
}) {
  const summary = report?.summary ?? {};
  const products = report?.products ?? [];
  const categoryRows = report?.categories ?? [];
  const totalPages = report?.totalPages ?? 0;
  const currentPage = report?.page ?? 1;

  return (
    <div className="inventory-health-page">
      <header className="ih-header">
        <div>
          <h1 className="page-title ih-title">
            <BarChart3 size={22} />
            Salud de inventario
          </h1>
          <p className="ih-subtitle">
            Rotacion comercial, stock muerto y capital inmovilizado. La ventana de ventas y el
            umbral de estancamiento se calculan en el servidor.
          </p>
        </div>
        <div className="ih-header-actions">
          <button type="button" className="btn btn-secondary" onClick={onRefresh} disabled={loading}>
            {loading ? <Loader2 size={16} className="ih-spin" /> : <RefreshCw size={16} />}
            Actualizar
          </button>
          <button
            type="button"
            className="btn btn-primary"
            onClick={onOpenPromotion}
            disabled={loading || Number(summary.deadStockCount) <= 0}
            title="Abrir la edicion multiple de precios del Paso 9 con los productos muertos"
          >
            <Tag size={16} />
            Aplicar Promocion / Descuento
          </button>
        </div>
      </header>

      <section className="card ih-filters">
        <div className="ih-filter-row">
          <label className="ih-field">
            <span>Categoria</span>
            <select
              className="form-select"
              value={filters.categoryId}
              onChange={(event) => onFilterChange({ categoryId: event.target.value, page: 1 })}
            >
              <option value="">Todas las categorias</option>
              {categories.map((category) => (
                <option key={category.value} value={category.value}>
                  {category.path || category.label}
                </option>
              ))}
            </select>
          </label>

          <label className="ih-field">
            <span>Ventana de ventas</span>
            <select
              className="form-select"
              value={filters.salesWindowDays}
              onChange={(event) => onFilterChange({ salesWindowDays: Number(event.target.value), page: 1 })}
            >
              {SALES_WINDOW_OPTIONS.map((days) => (
                <option key={days} value={days}>{days} dias</option>
              ))}
            </select>
          </label>

          <label className="ih-field">
            <span>Stock muerto desde</span>
            <select
              className="form-select"
              value={filters.deadStockDays}
              onChange={(event) => onFilterChange({ deadStockDays: Number(event.target.value), page: 1 })}
            >
              {DEAD_STOCK_OPTIONS.map((days) => (
                <option key={days} value={days}>{days} dias</option>
              ))}
            </select>
          </label>

          <label className="ih-field ih-field-search">
            <span>Producto o SKU</span>
            <input
              className="form-input"
              value={filters.searchText}
              placeholder="Buscar..."
              onChange={(event) => onFilterChange({ searchText: event.target.value, page: 1 })}
            />
          </label>

          <label className="ih-field">
            <span>Rotacion</span>
            <select
              className="form-select"
              value={filters.rotationClass}
              onChange={(event) => onFilterChange({ rotationClass: event.target.value, page: 1 })}
            >
              {ROTATION_CLASS_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </select>
          </label>
        </div>
      </section>

      {error && (
        <div className="ih-alert">
          <AlertTriangle size={16} />
          <span>{error}</span>
        </div>
      )}

      <section className="ih-kpis">
        <article className="ih-kpi ih-kpi-high">
          <span className="ih-kpi-label">ALTA ROTACION</span>
          <strong>{summary.highRotationCount ?? 0}</strong>
          <span>{formatHealthPercent(summary.highRotationPercent)}</span>
          <div className="ih-bar"><i style={{ width: `${clampHealthPercent(summary.highRotationPercent)}%` }} /></div>
        </article>
        <article className="ih-kpi ih-kpi-medium">
          <span className="ih-kpi-label">MEDIA ROTACION</span>
          <strong>{summary.mediumRotationCount ?? 0}</strong>
          <span>{formatHealthPercent(summary.mediumRotationPercent)}</span>
          <div className="ih-bar"><i style={{ width: `${clampHealthPercent(summary.mediumRotationPercent)}%` }} /></div>
        </article>
        <article className="ih-kpi ih-kpi-dead">
          <span className="ih-kpi-label">STOCK MUERTO</span>
          <strong>{summary.deadStockCount ?? 0}</strong>
          <span>{formatHealthPercent(summary.deadStockPercent)}</span>
          <div className="ih-bar"><i style={{ width: `${clampHealthPercent(summary.deadStockPercent)}%` }} /></div>
        </article>
        <article className="ih-kpi ih-kpi-capital">
          <span className="ih-kpi-label">CAPITAL INMOVILIZADO</span>
          <strong>{formatHealthMoney(summary.immobilizedCapitalUsd)}</strong>
          <span>{formatHealthPercent(summary.immobilizedCapitalPercent)} del inventario</span>
          <div className="ih-bar"><i style={{ width: `${clampHealthPercent(summary.immobilizedCapitalPercent)}%` }} /></div>
        </article>
      </section>

      <section className="ih-chart-card card">
        <div className="ih-chart-header">
          <div>
            <h2>Distribucion del inventario</h2>
            <p>{formatNumberEs(Number(summary.totalProducts) || 0, 0)} productos analizados</p>
          </div>
          <span>Valor total: {formatHealthMoney(summary.totalInventoryValueUsd)}</span>
        </div>
        <div className="ih-chart-rows">
          {[
            { label: 'Alta', value: summary.highRotationPercent, tone: 'high' },
            { label: 'Media', value: summary.mediumRotationPercent, tone: 'medium' },
            { label: 'Baja', value: summary.lowRotationPercent, tone: 'low' },
            { label: 'Inmovilizado', value: summary.immobilizedCapitalPercent, tone: 'dead' },
          ].map((row) => (
            <div className="ih-chart-row" key={row.label}>
              <span>{row.label}</span>
              <div className={`ih-bar ih-bar-${row.tone}`}>
                <i style={{ width: `${clampHealthPercent(row.value)}%` }} />
              </div>
              <strong>{formatHealthPercent(row.value)}</strong>
            </div>
          ))}
        </div>
      </section>

      <section className="card ih-table-card">
        <div className="ih-table-header">
          <h2>Productos</h2>
          <span>{report?.totalCount ?? 0} resultados</span>
        </div>
        <div className="ih-table-scroll">
          <table className="cart-table ih-table">
            <thead>
              <tr>
                <th>Producto</th>
                <th>Categoria</th>
                <th className="ih-right">Stock</th>
                <th className="ih-right">Valor USD</th>
                <th className="ih-right">Vendidos</th>
                <th className="ih-right">Vel./dia</th>
                <th className="ih-right">Dias inv.</th>
                <th>Ultima venta</th>
                <th className="ih-right">Dias sin venta</th>
                <th>Estado</th>
              </tr>
            </thead>
            <tbody>
              {products.map((product) => (
                <tr key={product.productId}>
                  <td>
                    <strong>{product.name}</strong>
                    <small>{product.sku}</small>
                  </td>
                  <td>{product.categoryName || 'Sin categoria'}</td>
                  <td className="ih-right">{formatNumberEs(product.stockQuantity, 2)}</td>
                  <td className="ih-right">{formatHealthMoney(product.inventoryValueUsd)}</td>
                  <td className="ih-right">{formatNumberEs(product.unitsSold, 1)}</td>
                  <td className="ih-right">{formatNumberEs(product.salesVelocityPerDay, 3)}</td>
                  <td className="ih-right">
                    {product.averageInventoryDays == null ? '—' : formatNumberEs(product.averageInventoryDays, 1)}
                  </td>
                  <td>
                    {product.lastSaleDateUtc
                      ? new Date(product.lastSaleDateUtc).toLocaleDateString('es-VE')
                      : '—'}
                  </td>
                  <td className="ih-right">{product.daysSinceLastSale}</td>
                  <td><span className={rotationBadgeClass(product.rotationClass)}>{product.rotationClassLabel}</span></td>
                </tr>
              ))}
            </tbody>
          </table>
          {inventoryHealthEmpty(report) && !loading && (
            <div className="ih-empty">Sin productos para los filtros seleccionados.</div>
          )}
        </div>
        <footer className="ih-pagination">
          <span>Pagina {currentPage} de {Math.max(totalPages, 1)}</span>
          <div>
            <button type="button" className="btn btn-secondary" onClick={onPreviousPage} disabled={currentPage <= 1 || loading}>
              <ChevronLeft size={16} /> Anterior
            </button>
            <button type="button" className="btn btn-secondary" onClick={onNextPage} disabled={currentPage >= totalPages || loading}>
              Siguiente <ChevronRight size={16} />
            </button>
          </div>
        </footer>
      </section>

      <section className="card ih-table-card">
        <div className="ih-table-header">
          <h2>Categorias</h2>
          <span>Agregado del universo filtrado</span>
        </div>
        <div className="ih-table-scroll">
          <table className="cart-table ih-table">
            <thead>
              <tr>
                <th>Categoria</th>
                <th className="ih-right">Productos</th>
                <th className="ih-right">Stock</th>
                <th className="ih-right">Valor USD</th>
                <th className="ih-right">Vendidos</th>
                <th className="ih-right">Dias inv.</th>
                <th className="ih-right">Muertos</th>
                <th className="ih-right">Capital inmov.</th>
                <th>Estado</th>
              </tr>
            </thead>
            <tbody>
              {categoryRows.map((category) => (
                <tr key={category.categoryId ?? 'none'}>
                  <td><strong>{category.categoryName}</strong></td>
                  <td className="ih-right">{category.productCount}</td>
                  <td className="ih-right">{formatNumberEs(category.stockQuantity, 2)}</td>
                  <td className="ih-right">{formatHealthMoney(category.inventoryValueUsd)}</td>
                  <td className="ih-right">{formatNumberEs(category.unitsSold, 1)}</td>
                  <td className="ih-right">
                    {category.averageInventoryDays == null ? '—' : formatNumberEs(category.averageInventoryDays, 1)}
                  </td>
                  <td className="ih-right">{category.deadStockCount}</td>
                  <td className="ih-right">{formatHealthMoney(category.immobilizedCapitalUsd)}</td>
                  <td><span className={rotationBadgeClass(category.rotationClass)}>{category.rotationClassLabel}</span></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      {loading && (
        <div className="ih-loading" role="status">
          <Loader2 size={22} className="ih-spin" />
          Calculando rotacion...
        </div>
      )}
    </div>
  );
}

function PromotionModal({ selection, percentage, onPercentageChange, onClose, onApply, applying, error }) {
  return (
    <div className="modal-overlay" role="dialog" aria-modal="true">
      <div className="modal-container ih-promotion-modal">
        <div className="modal-header">
          <h3 className="modal-title">Aplicar promocion / descuento</h3>
          <button type="button" className="modal-close-btn" onClick={onClose} aria-label="Cerrar">
            <X size={18} />
          </button>
        </div>
        <div className="modal-body">
          <p>
            Se aplicara una rebaja porcentual al precio Detal USD de{' '}
            <strong>{selection?.totalCount ?? 0}</strong> producto(s) en stock muerto.
          </p>
          <label className="ih-field">
            <span><Percent size={14} /> Descuento (%)</span>
            <input
              className="form-input"
              type="number"
              min="1"
              max="100"
              step="1"
              value={percentage}
              onChange={(event) => onPercentageChange(event.target.value)}
            />
          </label>
          {selection?.truncated && (
            <div className="ih-alert">
              <AlertTriangle size={16} />
              <span>
                La seleccion supera el maximo de 1000 productos del Paso 9. Refine la categoria o
                aumente el umbral.
              </span>
            </div>
          )}
          {error && (
            <div className="ih-alert">
              <AlertTriangle size={16} />
              <span>{error}</span>
            </div>
          )}
          <div className="ih-modal-actions">
            <button type="button" className="btn btn-secondary" onClick={onClose} disabled={applying}>Cancelar</button>
            <button
              type="button"
              className="btn btn-primary"
              onClick={onApply}
              disabled={applying || selection?.truncated || !(Number(percentage) > 0)}
            >
              {applying ? <Loader2 size={16} className="ih-spin" /> : <Tag size={16} />}
              Aplicar descuento
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

export default function InventoryHealthPage() {
  return (
    <RoleGuard view="inventory-health">
      <InventoryHealthPageContent />
    </RoleGuard>
  );
}

function InventoryHealthPageContent() {
  const [filters, setFilters] = useState({
    salesWindowDays: 90,
    deadStockDays: 60,
    categoryId: '',
    searchText: '',
    rotationClass: '',
    page: 1,
    pageSize: 20,
  });
  // El reporte es caro; se espera 350ms para que escribir en el buscador no dispare una
  // consulta por tecla. Los cambios de pagina/categoria tambien se benefician del mismo margen.
  const queryFilters = useDebounce(filters, 350);

  const [report, setReport] = useState(null);
  const [categories, setCategories] = useState([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [promotionSelection, setPromotionSelection] = useState(null);
  const [promotionPercent, setPromotionPercent] = useState(10);
  const [promotionError, setPromotionError] = useState('');
  const [applying, setApplying] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    getCategories(controller.signal)
      .then((data) => setCategories(toCategoryOptions(data)))
      .catch((loadError) => {
        if (loadError?.name !== 'AbortError') setError(loadError.message);
      });
    return () => controller.abort();
  }, []);

  const loadReport = useCallback(async (signal) => {
    setLoading(true);
    setError('');
    try {
      const data = await getInventoryHealthReport(queryFilters, signal);
      setReport(data);
    } catch (loadError) {
      if (loadError?.name !== 'AbortError') setError(loadError.message || 'No se pudo cargar el reporte.');
    } finally {
      setLoading(false);
    }
  }, [queryFilters]);

  useEffect(() => {
    const controller = new AbortController();
    loadReport(controller.signal);
    return () => controller.abort();
  }, [loadReport]);

  const handleFilterChange = (patch) => {
    setFilters((current) => ({ ...current, ...patch }));
  };

  const handleOpenPromotion = async () => {
    setPromotionError('');
    setNotice('');
    try {
      const selection = await getDeadStockSelection(queryFilters);
      setPromotionSelection(selection);
    } catch (selectionError) {
      setError(selectionError.message || 'No se pudo preparar la seleccion de stock muerto.');
    }
  };

  const handleApplyPromotion = async () => {
    if (!promotionSelection) return;
    setApplying(true);
    setPromotionError('');
    try {
      const result = await applyDeadStockPromotion(
        promotionSelection.productIds,
        Number(promotionPercent)
      );
      setNotice(`Promocion aplicada a ${result?.updatedCount ?? promotionSelection.totalCount} producto(s).`);
      setPromotionSelection(null);
      await loadReport();
    } catch (applyError) {
      setPromotionError(applyError.message || 'No se pudo aplicar la promocion.');
    } finally {
      setApplying(false);
    }
  };

  const pageSummary = useMemo(() => {
    if (!report) return '';
    return `Ventana ${filters.salesWindowDays} dias · stock muerto >= ${filters.deadStockDays} dias`;
  }, [filters.deadStockDays, filters.salesWindowDays, report]);

  return (
    <>
      <InventoryHealthDashboard
        report={report}
        loading={loading}
        error={error}
        filters={filters}
        categories={categories}
        onFilterChange={handleFilterChange}
        onRefresh={() => loadReport()}
        onOpenPromotion={handleOpenPromotion}
        onPreviousPage={() => handleFilterChange({ page: Math.max(1, filters.page - 1) })}
        onNextPage={() => handleFilterChange({ page: filters.page + 1 })}
      />
      {notice && <div className="ih-notice">{notice}</div>}
      {promotionSelection && (
        <PromotionModal
          selection={promotionSelection}
          percentage={promotionPercent}
          onPercentageChange={setPromotionPercent}
          onClose={() => setPromotionSelection(null)}
          onApply={handleApplyPromotion}
          applying={applying}
          error={promotionError}
        />
      )}
      <div className="ih-footer-note">{pageSummary}</div>
    </>
  );
}
