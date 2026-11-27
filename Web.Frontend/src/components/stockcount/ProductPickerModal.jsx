import { useEffect, useMemo, useState } from 'react';
import { Check, Loader2, Search, X } from 'lucide-react';
import Modal from '../ui/Modal';
import useDebounce from '../../hooks/useDebounce';
import { getProductsPaged } from '../../services/productsApi';
import './ProductPickerModal.css';

export const MAX_SCOPE_PRODUCTS = 1000;
export const PICKER_PAGE_SIZE = 50;

/** WU3: solo productos inventariables; nunca se muestran existencias ni costos. */
export function isCountablePickerProduct(product) {
  if (!product) return false;
  if (!product.isActive || product.isDeleted || product.isCashAdvance) return false;
  if (product.isGroupHeader && !product.isStockShared) return false;
  if (product.parentProductId != null && product.parentIsStockShared) return false;
  return true;
}

/**
 * Agrega/quita un producto de la selección respetando el tope. Devuelve la lista nueva y un
 * mensaje de error cuando se intenta superar el máximo.
 */
export function toggleScopeProduct(selected, product, max = MAX_SCOPE_PRODUCTS) {
  const current = Array.isArray(selected) ? selected : [];
  if (!product) return { selected: current, error: null };

  if (current.some((item) => item.id === product.id)) {
    return { selected: current.filter((item) => item.id !== product.id), error: null };
  }

  if (current.length >= max) {
    return { selected: current, error: `Máximo ${max} productos seleccionados.` };
  }

  return {
    selected: [...current, { id: product.id, sku: product.sku, name: product.name }],
    error: null,
  };
}

export function formatScopeCounter(count, max = MAX_SCOPE_PRODUCTS) {
  return `${Number(count) || 0} / ${max}`;
}

export default function ProductPickerModal({
  isOpen = true,
  categories = [],
  selectedProducts = [],
  onClose,
  onConfirm,
  maxSelection = MAX_SCOPE_PRODUCTS,
}) {
  const [selected, setSelected] = useState(() => (Array.isArray(selectedProducts) ? selectedProducts : []));
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebounce(search, 300);
  const [categoryId, setCategoryId] = useState('');
  const [page, setPage] = useState(1);
  const [items, setItems] = useState([]);
  const [totalCount, setTotalCount] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);
  const [selectionError, setSelectionError] = useState(null);

  const selectedIds = useMemo(() => new Set(selected.map((item) => item.id)), [selected]);
  const totalPages = Math.max(1, Math.ceil(totalCount / PICKER_PAGE_SIZE));

  const categoryOptions = useMemo(
    () => [
      { value: '', label: 'Todas las categorías' },
      ...(Array.isArray(categories)
        ? categories.map((category) => ({
            value: String(category.id),
            label: category.path || category.name || `Categoría ${category.id}`,
          }))
        : []),
    ],
    [categories]
  );

  useEffect(() => {
    if (!isOpen) return undefined;

    const controller = new AbortController();
    let cancelled = false;

    setLoading(true);
    setError(null);

    getProductsPaged(
      { page, pageSize: PICKER_PAGE_SIZE, filter: debouncedSearch, categoryId },
      controller.signal
    )
      .then((data) => {
        if (cancelled) return;
        const list = Array.isArray(data?.items) ? data.items : [];
        setItems(list.filter(isCountablePickerProduct));
        setTotalCount(Number(data?.totalCount) || 0);
      })
      .catch((err) => {
        if (cancelled || err?.name === 'AbortError') return;
        console.error('[ProductPickerModal] Error cargando productos:', err);
        setError(err?.message || 'No se pudieron cargar los productos.');
        setItems([]);
        setTotalCount(0);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [isOpen, page, debouncedSearch, categoryId]);

  const handleSearchChange = (event) => {
    setSearch(event.target.value);
    setPage(1);
  };

  const handleCategoryChange = (event) => {
    setCategoryId(event.target.value);
    setPage(1);
  };

  const handleToggle = (product) => {
    const result = toggleScopeProduct(selected, product, maxSelection);
    setSelected(result.selected);
    setSelectionError(result.error);
  };

  const handleRemove = (productId) => {
    setSelected((prev) => prev.filter((item) => item.id !== productId));
    setSelectionError(null);
  };

  const handleConfirm = () => {
    if (selected.length === 0) {
      setSelectionError('Seleccione al menos un producto para el alcance.');
      return;
    }
    onConfirm?.(selected);
  };

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Seleccionar productos" maxWidth="960px">
      <div className="pp-modal">
        <p className="pp-subtitle">
          Solo se podrán escanear los productos seleccionados. El conteo permanece ciego: no se muestran
          existencias ni costos.
        </p>

        <div className="pp-toolbar">
          <div className="pp-search">
            <Search size={16} aria-hidden="true" />
            <input
              type="search"
              className="form-input"
              value={search}
              onChange={handleSearchChange}
              placeholder="Buscar por nombre o SKU / código de barras"
              aria-label="Buscar productos por nombre o SKU"
            />
          </div>
          <select
            className="form-select pp-category"
            value={categoryId}
            onChange={handleCategoryChange}
            aria-label="Filtrar productos por categoría"
          >
            {categoryOptions.map((option) => (
              <option key={option.value || 'all'} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
          <span className="pp-counter" aria-live="polite">
            {formatScopeCounter(selected.length, maxSelection)}
          </span>
        </div>

        <div className="pp-content">
          <div className="pp-results" aria-live="polite">
            {loading ? (
              <div className="pp-state">
                <Loader2 size={20} className="spin" aria-hidden="true" /> Cargando productos…
              </div>
            ) : error ? (
              <div className="pp-state pp-error" role="alert">
                {error}
              </div>
            ) : items.length === 0 ? (
              <div className="pp-state">No se encontraron productos contables con ese filtro.</div>
            ) : (
              <ul className="pp-list">
                {items.map((product) => {
                  const checked = selectedIds.has(product.id);
                  return (
                    <li key={product.id}>
                      <label className={`pp-row${checked ? ' is-selected' : ''}`}>
                        <input
                          type="checkbox"
                          checked={checked}
                          onChange={() => handleToggle(product)}
                        />
                        <span className="pp-sku">{product.sku}</span>
                        <span className="pp-name">{product.name}</span>
                        {checked && <Check size={16} className="pp-check" aria-hidden="true" />}
                      </label>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>

          <aside className="pp-selected" aria-label="Productos seleccionados">
            <div className="pp-selected-head">
              <strong>Seleccionados</strong>
              <span>{formatScopeCounter(selected.length, maxSelection)}</span>
            </div>
            {selected.length === 0 ? (
              <p className="pp-empty">Aún no has seleccionado productos.</p>
            ) : (
              <ul className="pp-chips">
                {selected.map((item) => (
                  <li key={item.id} className="pp-chip">
                    <span className="pp-chip-name">{item.name}</span>
                    <span className="pp-chip-sku">{item.sku}</span>
                    <button
                      type="button"
                      className="pp-chip-remove"
                      onClick={() => handleRemove(item.id)}
                      aria-label={`Quitar ${item.name}`}
                    >
                      <X size={12} aria-hidden="true" />
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </aside>
        </div>

        {selectionError && (
          <div className="pp-selection-error" role="alert">
            {selectionError}
          </div>
        )}

        <div className="pp-footer">
          <div className="pp-pagination">
            <button
              type="button"
              className="btn btn-outline btn-sm"
              onClick={() => setPage((current) => Math.max(1, current - 1))}
              disabled={page <= 1 || loading}
            >
              Anterior
            </button>
            <span>
              Página {page} de {totalPages} · {totalCount} productos activos
            </span>
            <button
              type="button"
              className="btn btn-outline btn-sm"
              onClick={() => setPage((current) => Math.min(totalPages, current + 1))}
              disabled={page >= totalPages || loading}
            >
              Siguiente
            </button>
          </div>
          <div className="pp-actions">
            <button type="button" className="btn btn-outline" onClick={onClose}>
              Cancelar
            </button>
            <button
              type="button"
              className="btn btn-primary"
              onClick={handleConfirm}
              disabled={selected.length === 0}
            >
              Confirmar selección ({selected.length})
            </button>
          </div>
        </div>
      </div>
    </Modal>
  );
}
