import { useState, useEffect, useCallback } from 'react';
import { api } from '../services/api';
import { Package, Search, Loader2, RefreshCw, Tag, ArrowUpDown, ArrowUp, ArrowDown } from 'lucide-react';
import { useExchangeRate } from '../context/ExchangeRateContext';
import { formatBsS, formatUSD } from '../utils/formatters';
import useDebounce from '../hooks/useDebounce';
import Pagination from '../components/ui/Pagination';
import './CatalogPage.css';

export default function CatalogPage() {
  const { exchangeRate } = useExchangeRate();
  const [products, setProducts] = useState([]);
  const [loading, setLoading] = useState(false);
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebounce(search, 300);
  
  // Filtro de Moneda: "Bs.S" por defecto al entrar al catálogo
  const [currency, setCurrency] = useState('Bs.S');

  // Botón desactivado por defecto al entrar al catálogo
  const [showWholesale, setShowWholesale] = useState(false);

  // Ordenamiento dinámico
  const [sortBy, setSortBy] = useState('name');
  const [sortDescending, setSortDescending] = useState(false);

  // Paginación de 25 elementos por página
  const [currentPage, setCurrentPage] = useState(1);
  const [totalCount, setTotalCount] = useState(0);
  const [totalPages, setTotalPages] = useState(1);
  const pageSize = 25;

  // 8.7-M9: un SOLO efecto orquestador por página con AbortController. Los handlers de
  // sort/paginación/búsqueda solo cambian estado; el efecto dispara un único fetch por cambio.
  const [reloadToken, setReloadToken] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    const filterParam = debouncedSearch ? `&filter=${encodeURIComponent(debouncedSearch)}` : '';
    const sortParam = sortBy ? `&sortBy=${encodeURIComponent(sortBy)}&isDescending=${sortDescending}` : '';

    api.get(`/api/products?page=${currentPage}&pageSize=${pageSize}${filterParam}${sortParam}`, controller.signal)
      .then((data) => {
        const items = data?.items || (Array.isArray(data) ? data : []);
        const total = data?.totalCount ?? items.length;
        const pages = data?.totalPages ?? (Math.ceil(total / pageSize) || 1);

        setProducts(items);
        setTotalCount(total);
        setTotalPages(pages);

        window.scrollTo({ top: 0, behavior: 'smooth' });
        const mainContent = document.querySelector('.app-content') || document.querySelector('.catalog-page');
        if (mainContent) {
          mainContent.scrollTo({ top: 0, behavior: 'smooth' });
        }
      })
      .catch((err) => {
        if (err?.name !== 'AbortError') {
          console.error('[CatalogPage] Error cargando catálogo:', err);
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });

    return () => controller.abort();
  }, [debouncedSearch, sortBy, sortDescending, currentPage, reloadToken]);

  const handleSort = useCallback((column) => {
    if (sortBy === column) {
      setSortDescending((d) => !d);
    } else {
      setSortBy(column);
      setSortDescending(false);
    }
    setCurrentPage(1);
  }, [sortBy]);

  const renderSortIcon = (column) => {
    if (sortBy !== column) {
      return <ArrowUpDown size={14} className="text-muted cat-sort-icon-muted" />;
    }
    return sortDescending
      ? <ArrowDown size={14} className="color-primary cat-sort-icon" />
      : <ArrowUp size={14} className="color-primary cat-sort-icon" />;
  };

  const handleSearchChange = (e) => {
    setSearch(e.target.value);
  };

  const handleSearchSubmit = (e) => {
    e.preventDefault();
    setCurrentPage(1);
  };

  return (
    <div className="catalog-page">
      {/* ── Encabezado Principal ── */}
      <div className="catalog-header flex-between mb-4">
        <h2 className="catalog-title flex-align-center gap-2">
          <Package size={26} className="color-primary flex-shrink-0" />
          <span>Catálogo de Productos</span>
        </h2>
        <div className="flex-align-center gap-2">
          <button
            type="button"
            className="btn btn-outline btn-sm flex-align-center cat-gap-6"
            onClick={() => setReloadToken((t) => t + 1)}
            disabled={loading}
          >
            <RefreshCw size={16} className={loading ? 'animate-spin' : ''} /> Actualizar
          </button>
        </div>
      </div>

      {/* ── Barra de Búsqueda, Selector de Moneda y Botón de Precio al Mayor ── */}
      <div className="card p-3 cat-card-mb">
        <div className="catalog-controls-row d-flex align-center gap-3 w-full">
          <form onSubmit={handleSearchSubmit} className="flex-1 w-full">
            <div className="form-group mb-0 cat-search-wrap w-full">
              <input
                id="catalog-search-input"
                type="text"
                className="form-control cat-search-input"
                placeholder="Buscar por nombre o SKU..."
                value={search}
                onChange={handleSearchChange}
              />
              <Search size={18} className="cat-search-icon opacity-50" />
            </div>
          </form>

          {/* ── 1. Selector Desplegable de Moneda (Bs.S por defecto) ── */}
          <div className="flex-align-center cat-gap-6">
            <span className="text-xs text-muted font-medium text-nowrap">Moneda:</span>
            <select
              className="form-control form-control-sm font-bold cat-select-currency"
              value={currency}
              onChange={(e) => setCurrency(e.target.value)}
            >
              <option value="Bs.S">Bs.S</option>
              <option value="USD">USD ($)</option>
            </select>
          </div>

          {/* ── 2. Selector de Ordenamiento ── */}
          <div className="flex-align-center cat-gap-6">
            <span className="text-xs text-muted font-medium text-nowrap">Ordenar:</span>
            <select
              className="form-control form-control-sm cat-select-sort"
              value={`${sortBy}_${sortDescending ? 'desc' : 'asc'}`}
              onChange={(e) => {
                const [col, dir] = e.target.value.split('_');
                const isDesc = dir === 'desc';
                setSortBy(col);
                setSortDescending(isDesc);
                setCurrentPage(1);
              }}
            >
              <option value="name_asc">Nombre (A-Z)</option>
              <option value="name_desc">Nombre (Z-A)</option>
              <option value="price_asc">Precio (Menor a Mayor)</option>
              <option value="price_desc">Precio (Mayor a Menor)</option>
              <option value="stock_asc">Stock (Menor a Mayor)</option>
              <option value="stock_desc">Stock (Mayor a Menor)</option>
              <option value="sku_asc">SKU (Menos dígitos)</option>
              <option value="sku_desc">SKU (Más dígitos)</option>
            </select>
          </div>

          {/* Botón para mostrar / ocultar Precios al Mayor (Desactivado por defecto) */}
          <button
            type="button"
            className={`btn ${showWholesale ? 'btn-primary' : 'btn-outline'} btn-sm catalog-wholesale-toggle-btn cat-wholesale-btn`}
            onClick={() => setShowWholesale(!showWholesale)}
          >
            <Tag size={16} /> {showWholesale ? 'Ocultar Precios al Mayor' : 'Mostrar Precios al Mayor'}
          </button>
        </div>
      </div>

      {/* ── Estado de Carga / Sin Resultados ── */}
      {loading ? (
        <div className="card text-center flex-column flex-align-center justify-center gap-3 cat-loading-pad">
          <Loader2 size={36} className="animate-spin color-primary mx-auto" />
          <p className="text-muted font-medium">Cargando productos del catálogo...</p>
        </div>
      ) : products.length === 0 ? (
        <div className="card text-center text-muted cat-empty-pad">
          <Package size={48} className="mx-auto mb-3 text-muted opacity-50" />
          <h3 className="font-bold text-lg mb-1 cat-empty-title">No hay productos encontrados</h3>
          <p className="text-sm cat-empty-text">
            {search ? 'No se encontraron productos que coincidan con la búsqueda.' : 'El inventario de productos está vacío.'}
          </p>
        </div>
      ) : (
        <>
          {/* ── 3A. VISTA ESCRITORIO (COLUMNAS DE PRECIO SEGÚN MONEDA SELECCIONADA) ── */}
          <div className="catalog-desktop-view card padding-none overflow-hidden cat-desktop-card">
            <table className="cart-table text-left">
              <thead>
                <tr className="cat-th-row">
                  <th
                    className="cat-th cat-th-sku"
                    onClick={() => handleSort('sku')}
                    title="Ordenar por cantidad de dígitos del código de barras"
                  >
                    <div className="d-inline-flex align-center gap-1">
                      SKU {renderSortIcon('sku')}
                    </div>
                  </th>
                  <th
                    className="cat-th"
                    onClick={() => handleSort('name')}
                    title="Ordenar alfabéticamente por nombre"
                  >
                    <div className="d-inline-flex align-center gap-1">
                      Producto {renderSortIcon('name')}
                    </div>
                  </th>
                  <th
                    className="cat-th text-right text-nowrap"
                    onClick={() => handleSort('price')}
                    title="Ordenar por precio al detal"
                  >
                    <div className="d-inline-flex align-center justify-end gap-1">
                      Precio Detal ({currency}) {renderSortIcon('price')}
                    </div>
                  </th>
                  
                  {/* Columnas dinámicas de Precio al Mayor */}
                  {showWholesale && (
                    <>
                      <th className="cat-th-wholesale-price">
                        Precio Mayor ({currency})
                      </th>
                      <th className="cat-th-wholesale-min">
                        Cant. Mín. Mayor
                      </th>
                    </>
                  )}

                  <th
                    className="cat-th text-center cat-th-stock"
                    onClick={() => handleSort('stock')}
                    title="Ordenar por cantidad en stock"
                  >
                    <div className="d-inline-flex align-center justify-center gap-1">
                      Stock {renderSortIcon('stock')}
                    </div>
                  </th>
                </tr>
              </thead>
              <tbody>
                {products.map((p) => {
                  const retailUSD = p.priceUSD || 0;
                  const retailBsS = p.priceUSD > 0 ? p.priceUSD * exchangeRate : (p.priceBsS || 0);

                  // Regla 2: ¿Tiene descuento de precio al mayor real configurado?
                  const hasRealWholesale = (p.hasWholesale || p.priceWholesaleUSD > 0) && p.priceWholesaleUSD > 0 && p.priceWholesaleUSD < retailUSD;

                  // Regla 2: Si NO tiene precio al mayor, hereda el precio al detal
                  const wholesaleUSD = hasRealWholesale ? p.priceWholesaleUSD : retailUSD;
                  const wholesaleBsS = hasRealWholesale ? p.priceWholesaleUSD * exchangeRate : retailBsS;

                  // Regla 3: Si hereda detal, unidades mínimas por defecto en "1" (en lugar de "0"), siempre entero sin decimales
                  const minQty = Math.round(hasRealWholesale ? (p.minWholesaleQuantity || 1) : 1);

                  // Regla 3: Tono Naranja (#D97706) si hereda detal, Violeta/Primario (#6366f1) si aplica descuento
                  const wholesaleColor = hasRealWholesale ? 'var(--primary-color, #6366f1)' : '#D97706';

                  const stockQty = p.isGroupHeader ? (p.consolidatedStock ?? 0) : (p.stockQuantity ?? p.stock ?? 0);
                  const unitStr = p.unitOfMeasureStr || (p.unitOfMeasure !== undefined && p.unitOfMeasure !== 0 ? p.unitOfMeasure : 'Und');

                  const isIndepParent = p.isGroupHeader && p.hasIndependentPricing;

                  // Formato según Moneda seleccionada
                  const displayRetail = isIndepParent ? '—' : (currency === 'USD' ? formatUSD(retailUSD) : formatBsS(retailBsS));
                  const displayWholesale = isIndepParent ? '—' : (currency === 'USD' ? formatUSD(wholesaleUSD) : formatBsS(wholesaleBsS));

                  return (
                    <tr key={p.id} className="cat-td-row">
                      <td className="cat-td font-mono text-muted">{p.sku || '-'}</td>
                      <td className="cat-td font-medium">
                        <div className="d-inline-flex align-center gap-2 flex-wrap">
                          <span>{p.name}</span>
                          {p.isGroupHeader && (
                            <>
                              <span className="badge-variant-group">
                                {p.variantCount > 0 ? `${p.variantCount} variantes` : 'Grupo'}
                              </span>
                              {p.isStockShared && (
                                <span className="badge cat-badge-shared" title="Todas las presentaciones descuentan del stock centralizado del padre">
                                  Stock Compartido
                                </span>
                              )}
                              {p.hasIndependentPricing && (
                                <span className="badge cat-badge-indep" title="Cada presentación define su costo y precio individual">
                                  Precios Indep.
                                </span>
                              )}
                            </>
                          )}
                        </div>
                      </td>
                      <td className="cat-td text-right text-nowrap font-mono font-bold" title={isIndepParent ? 'Precios individuales definidos en cada variante' : undefined}>
                        {displayRetail}
                      </td>

                      {/* Celdas dinámicas de Precio al Mayor */}
                      {showWholesale && (
                        <>
                          <td className="cat-td-wholesale-price font-bold font-mono" title={isIndepParent ? 'Precios individuales definidos en cada variante' : undefined}>
                            <span style={{ color: isIndepParent ? 'inherit' : wholesaleColor }}>
                              {displayWholesale}
                            </span>
                            {!isIndepParent && !hasRealWholesale && (
                              <span className="cat-wholesale-detal-note">
                                (Detal)
                              </span>
                            )}
                          </td>
                          <td className="cat-td-wholesale-min font-bold">
                            <span style={{ color: (isIndepParent || hasRealWholesale) ? 'inherit' : '#D97706' }}>
                              {isIndepParent ? '—' : `${minQty} ${unitStr}`}
                            </span>
                          </td>
                        </>
                      )}

                      <td className="cat-td text-center">
                        {p.isCashAdvance ? (
                          <span className="badge cat-badge-service">
                            Servicio
                          </span>
                        ) : (
                          <>
                            <span 
                              className={`badge ${stockQty > 0 ? 'badge-success' : 'badge-danger'}`} 
                              title={p.isGroupHeader 
                                ? (p.isStockShared ? 'Inventario centralizado en el producto padre' : 'Suma consolidada de todas las presentaciones') 
                                : (p.parentProductId && p.parentIsStockShared ? 'Inventario centralizado del producto padre' : 'Inventario disponible')}
                            >
                              {stockQty} {unitStr !== 'Und' ? unitStr : ''}
                            </span>
                            {p.isGroupHeader && (
                              <div className="cat-consolidated-note">
                                (Consolidado)
                              </div>
                            )}
                          </>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>

          {/* ── 3B. VISTA MÓVIL (CARD LAYOUT CON FILTRO DE MONEDA Y ALERTAS VISUALES) ── */}
          <div className="catalog-mobile-view">
            {products.map((p) => {
              const retailUSD = p.priceUSD || 0;
              const retailBsS = p.priceUSD > 0 ? p.priceUSD * exchangeRate : (p.priceBsS || 0);

              const hasRealWholesale = (p.hasWholesale || p.priceWholesaleUSD > 0) && p.priceWholesaleUSD > 0 && p.priceWholesaleUSD < retailUSD;
              const wholesaleUSD = hasRealWholesale ? p.priceWholesaleUSD : retailUSD;
              const wholesaleBsS = hasRealWholesale ? p.priceWholesaleUSD * exchangeRate : retailBsS;
              const minQty = Math.round(hasRealWholesale ? (p.minWholesaleQuantity || 1) : 1);
              const wholesaleColor = hasRealWholesale ? 'var(--primary-color, #6366f1)' : '#D97706';

              const stockQty = p.isGroupHeader ? (p.consolidatedStock ?? 0) : (p.stockQuantity ?? p.stock ?? 0);
              const unitStr = p.unitOfMeasureStr || (p.unitOfMeasure !== undefined && p.unitOfMeasure !== 0 ? p.unitOfMeasure : 'Und');

              const displayRetail = currency === 'USD' ? formatUSD(retailUSD) : formatBsS(retailBsS);
              const displayWholesale = currency === 'USD' ? formatUSD(wholesaleUSD) : formatBsS(wholesaleBsS);

              return (
                <div key={p.id} className="catalog-mobile-card">
                  {/* Renglón 1: SKU a la izquierda, STOCK a la derecha */}
                  <div className="catalog-mobile-card-row1">
                    <span className="font-mono text-xs text-muted">
                      SKU: <strong>{p.sku || '-'}</strong>
                    </span>
                    {p.isCashAdvance ? (
                      <span className="badge cat-badge-service-lg">
                        Servicio
                      </span>
                    ) : (
                      <span className={`badge ${stockQty > 0 ? 'badge-success' : 'badge-danger'} cat-badge-stock-lg`}>
                        {p.isGroupHeader ? `Total: ${stockQty}` : `Stock: ${stockQty}`} {unitStr}
                      </span>
                    )}
                  </div>

                  {/* Renglón 2: PRODUCTO en texto grande y negrita */}
                  <div className="catalog-mobile-card-title">
                    <div className="d-flex align-center cat-gap-6 flex-wrap">
                      <span>{p.name || p.productName || '-'}</span>
                      {p.isGroupHeader && (
                        <>
                          <span className="badge-variant-group">
                            {p.variantCount > 0 ? `${p.variantCount} variantes` : 'Grupo'}
                          </span>
                          {p.isStockShared && (
                            <span className="badge cat-badge-shared">
                              Stock Compartido
                            </span>
                          )}
                          {p.hasIndependentPricing && (
                            <span className="badge cat-badge-indep">
                              Precios Indep.
                            </span>
                          )}
                        </>
                      )}
                    </div>
                  </div>

                  {/* Renglón 3: PRECIO DETAL en Moneda seleccionada */}
                  <div className="catalog-mobile-card-prices">
                    <div className="d-flex flex-column">
                      <span className="text-xs text-muted">Precio Detal ({currency})</span>
                      <span className="font-bold font-mono text-nowrap text-base">
                        {displayRetail}
                      </span>
                    </div>

                    <div className="text-right d-flex flex-column align-end">
                      <span className="text-xs text-muted">Stock Disponible</span>
                      <span className="font-bold cat-mobile-stock-num">
                        {stockQty} {unitStr}
                      </span>
                    </div>
                  </div>

                  {/* Renglón 4 (Opcional): PRECIO AL MAYOR Y CANTIDAD MÍNIMA en Móvil */}
                  {showWholesale && (
                    <div className="catalog-wholesale-card-box mt-2" style={{ borderColor: hasRealWholesale ? 'rgba(99, 102, 241, 0.2)' : 'rgba(217, 119, 6, 0.3)' }}>
                      <div className="d-flex justify-between align-center">
                        <div>
                          <div className="text-xs text-muted">
                            Precio al Mayor ({currency})
                            {!hasRealWholesale && <span className="cat-no-discount-note">(Sin Descuento)</span>}
                          </div>
                          <div className="font-bold font-mono text-nowrap cat-wholesale-num" style={{ color: wholesaleColor }}>
                            {displayWholesale}
                          </div>
                        </div>
                        <div className="text-right">
                          <div className="text-xs text-muted">Cant. Mínima</div>
                          <div className="font-bold text-nowrap" style={{ color: hasRealWholesale ? 'inherit' : '#D97706' }}>
                            {minQty} {unitStr}
                          </div>
                        </div>
                      </div>
                    </div>
                  )}
                </div>
              );
            })}
          </div>

          {/* ── 5. BARRA DE PAGINACIÓN AVANZADA CENTRADA (Ambas Versiones) ── */}
          <Pagination
            currentPage={currentPage}
            totalPages={totalPages}
            totalCount={totalCount}
            onPageChange={(p) => setCurrentPage(p)}
            loading={loading}
            itemLabel="productos"
          />
        </>
      )}
    </div>
  );
}
