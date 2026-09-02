import { useState, useEffect, useCallback } from 'react';
import { api } from '../services/api';
import { Package, Search, Loader2, RefreshCw, ChevronLeft, ChevronRight, Tag, DollarSign, ArrowUpDown, ArrowUp, ArrowDown } from 'lucide-react';
import { useExchangeRate } from '../context/ExchangeRateContext';
import { formatBsS, formatUSD } from '../utils/formatters';
import useDebounce from '../hooks/useDebounce';
import Pagination from '../components/ui/Pagination';

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

  const loadProducts = useCallback(async (filter = '', page = 1, sort = sortBy, desc = sortDescending) => {
    setLoading(true);
    try {
      const filterParam = filter ? `&filter=${encodeURIComponent(filter)}` : '';
      const sortParam = sort ? `&sortBy=${encodeURIComponent(sort)}&isDescending=${desc}` : '';
      const data = await api.get(`/api/products?page=${page}&pageSize=${pageSize}${filterParam}${sortParam}`);
      
      const items = data?.items || (Array.isArray(data) ? data : []);
      const total = data?.totalCount ?? items.length;
      const pages = data?.totalPages ?? (Math.ceil(total / pageSize) || 1);

      setProducts(items);
      setTotalCount(total);
      setTotalPages(pages);
      setCurrentPage(page);

      // Auto-scroll al inicio de la tabla/vista
      window.scrollTo({ top: 0, behavior: 'smooth' });
      const mainContent = document.querySelector('.app-content') || document.querySelector('.catalog-page');
      if (mainContent) {
        mainContent.scrollTo({ top: 0, behavior: 'smooth' });
      }
    } catch (err) {
      console.error('[CatalogPage] Error cargando catálogo:', err);
    } finally {
      setLoading(false);
    }
  }, [sortBy, sortDescending]);

  useEffect(() => {
    loadProducts(debouncedSearch, 1, sortBy, sortDescending);
  }, [debouncedSearch, sortBy, sortDescending, loadProducts]);

  const handleSort = useCallback((column) => {
    let newDesc = false;
    if (sortBy === column) {
      newDesc = !sortDescending;
      setSortDescending(newDesc);
    } else {
      setSortBy(column);
      setSortDescending(false);
    }
    loadProducts(debouncedSearch, 1, column, newDesc);
  }, [sortBy, sortDescending, debouncedSearch, loadProducts]);

  const renderSortIcon = (column) => {
    if (sortBy !== column) {
      return <ArrowUpDown size={14} className="text-muted" style={{ opacity: 0.35, marginLeft: '5px', verticalAlign: 'middle' }} />;
    }
    return sortDescending
      ? <ArrowDown size={14} className="color-primary" style={{ marginLeft: '5px', verticalAlign: 'middle' }} />
      : <ArrowUp size={14} className="color-primary" style={{ marginLeft: '5px', verticalAlign: 'middle' }} />;
  };

  const handleSearchChange = (e) => {
    setSearch(e.target.value);
  };

  const handleSearchSubmit = (e) => {
    e.preventDefault();
    loadProducts(debouncedSearch, 1);
  };

  const handlePrevPage = () => {
    if (currentPage > 1) {
      loadProducts(search, currentPage - 1);
    }
  };

  const handleNextPage = () => {
    if (currentPage < totalPages) {
      loadProducts(search, currentPage + 1);
    }
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
            className="btn btn-outline btn-sm flex-align-center gap-2"
            onClick={() => loadProducts(search, currentPage)}
            disabled={loading}
            style={{ display: 'flex', alignItems: 'center', gap: '6px' }}
          >
            <RefreshCw size={16} className={loading ? 'animate-spin' : ''} /> Actualizar
          </button>
        </div>
      </div>

      {/* ── Barra de Búsqueda, Selector de Moneda y Botón de Precio al Mayor ── */}
      <div className="card mb-4 p-3" style={{ marginBottom: '20px' }}>
        <div className="catalog-controls-row" style={{ display: 'flex', gap: '12px', alignItems: 'center', width: '100%' }}>
          <form onSubmit={handleSearchSubmit} style={{ flex: 1, width: '100%' }}>
            <div className="form-group mb-0" style={{ position: 'relative', width: '100%' }}>
              <input
                id="catalog-search-input"
                type="text"
                className="form-control"
                placeholder="Buscar por nombre o SKU..."
                value={search}
                onChange={handleSearchChange}
                style={{ paddingLeft: '38px', width: '100%' }}
              />
              <Search size={18} style={{ position: 'absolute', left: '12px', top: '50%', transform: 'translateY(-50%)', opacity: 0.5 }} />
            </div>
          </form>

          {/* ── 1. Selector Desplegable de Moneda (Bs.S por defecto) ── */}
          <div style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
            <span className="text-xs text-muted font-medium" style={{ whiteSpace: 'nowrap' }}>Moneda:</span>
            <select
              className="form-control form-control-sm"
              value={currency}
              onChange={(e) => setCurrency(e.target.value)}
              style={{ fontWeight: 700, padding: '8px 12px', borderRadius: '8px', cursor: 'pointer', minWidth: '95px' }}
            >
              <option value="Bs.S">Bs.S</option>
              <option value="USD">USD ($)</option>
            </select>
          </div>

          {/* ── 2. Selector de Ordenamiento ── */}
          <div style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
            <span className="text-xs text-muted font-medium" style={{ whiteSpace: 'nowrap' }}>Ordenar:</span>
            <select
              className="form-control form-control-sm"
              value={`${sortBy}_${sortDescending ? 'desc' : 'asc'}`}
              onChange={(e) => {
                const [col, dir] = e.target.value.split('_');
                const isDesc = dir === 'desc';
                setSortBy(col);
                setSortDescending(isDesc);
                loadProducts(debouncedSearch, 1, col, isDesc);
              }}
              style={{ padding: '8px 12px', borderRadius: '8px', cursor: 'pointer', minWidth: '130px' }}
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
            className={`btn ${showWholesale ? 'btn-primary' : 'btn-outline'} btn-sm catalog-wholesale-toggle-btn`}
            onClick={() => setShowWholesale(!showWholesale)}
            style={{ display: 'inline-flex', alignItems: 'center', gap: '8px', whiteSpace: 'nowrap', padding: '10px 16px', fontWeight: 600 }}
          >
            <Tag size={16} /> {showWholesale ? 'Ocultar Precios al Mayor' : 'Mostrar Precios al Mayor'}
          </button>
        </div>
      </div>

      {/* ── Estado de Carga / Sin Resultados ── */}
      {loading ? (
        <div className="card p-5 text-center flex-column flex-align-center justify-center gap-3" style={{ padding: '60px', textAlign: 'center' }}>
          <Loader2 size={36} className="animate-spin color-primary mx-auto" />
          <p className="text-muted font-medium">Cargando productos del catálogo...</p>
        </div>
      ) : products.length === 0 ? (
        <div className="card p-5 text-center text-muted" style={{ padding: '40px', textAlign: 'center' }}>
          <Package size={48} className="mx-auto mb-3 text-muted" style={{ opacity: 0.5, margin: '0 auto 12px auto' }} />
          <h3 className="font-bold text-lg mb-1" style={{ fontSize: '1.2rem', fontWeight: 700 }}>No hay productos encontrados</h3>
          <p className="text-sm" style={{ opacity: 0.7 }}>
            {search ? 'No se encontraron productos que coincidan con la búsqueda.' : 'El inventario de productos está vacío.'}
          </p>
        </div>
      ) : (
        <>
          {/* ── 3A. VISTA ESCRITORIO (COLUMNAS DE PRECIO SEGÚN MONEDA SELECCIONADA) ── */}
          <div className="catalog-desktop-view card padding-none overflow-hidden" style={{ borderRadius: '12px', border: '1px solid var(--border-color)', overflow: 'hidden' }}>
            <table className="cart-table" style={{ width: '100%', borderCollapse: 'collapse', textAlign: 'left' }}>
              <thead>
                <tr style={{ borderBottom: '1px solid var(--border-color)', backgroundColor: 'var(--bg-secondary, rgba(255,255,255,0.03))' }}>
                  <th 
                    style={{ padding: '14px', width: '130px', cursor: 'pointer', userSelect: 'none' }}
                    onClick={() => handleSort('sku')}
                    title="Ordenar por cantidad de dígitos del código de barras"
                  >
                    <div style={{ display: 'inline-flex', alignItems: 'center', gap: '4px' }}>
                      SKU {renderSortIcon('sku')}
                    </div>
                  </th>
                  <th 
                    style={{ padding: '14px', cursor: 'pointer', userSelect: 'none' }}
                    onClick={() => handleSort('name')}
                    title="Ordenar alfabéticamente por nombre"
                  >
                    <div style={{ display: 'inline-flex', alignItems: 'center', gap: '4px' }}>
                      Producto {renderSortIcon('name')}
                    </div>
                  </th>
                  <th 
                    style={{ padding: '14px', textAlign: 'right', whiteSpace: 'nowrap', cursor: 'pointer', userSelect: 'none' }}
                    onClick={() => handleSort('price')}
                    title="Ordenar por precio al detal"
                  >
                    <div style={{ display: 'inline-flex', alignItems: 'center', justifyContent: 'flex-end', gap: '4px' }}>
                      Precio Detal ({currency}) {renderSortIcon('price')}
                    </div>
                  </th>
                  
                  {/* Columnas dinámicas de Precio al Mayor */}
                  {showWholesale && (
                    <>
                      <th style={{ padding: '14px', textAlign: 'right', whiteSpace: 'nowrap', color: 'var(--primary-color, #6366f1)', backgroundColor: 'rgba(99, 102, 241, 0.06)' }}>
                        Precio Mayor ({currency})
                      </th>
                      <th style={{ padding: '14px', textAlign: 'center', whiteSpace: 'nowrap', backgroundColor: 'rgba(99, 102, 241, 0.06)' }}>
                        Cant. Mín. Mayor
                      </th>
                    </>
                  )}

                  <th 
                    style={{ padding: '14px', textAlign: 'center', width: '110px', cursor: 'pointer', userSelect: 'none' }}
                    onClick={() => handleSort('stock')}
                    title="Ordenar por cantidad en stock"
                  >
                    <div style={{ display: 'inline-flex', alignItems: 'center', justifyContent: 'center', gap: '4px' }}>
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
                    <tr key={p.id} style={{ borderBottom: '1px solid var(--border-color)' }}>
                      <td style={{ padding: '14px' }} className="font-mono text-muted">{p.sku || '-'}</td>
                      <td style={{ padding: '14px' }} className="font-medium">
                        <div style={{ display: 'inline-flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' }}>
                          <span>{p.name}</span>
                          {p.isGroupHeader && (
                            <>
                              <span className="badge-variant-group">
                                {p.variantCount > 0 ? `${p.variantCount} variantes` : 'Grupo'}
                              </span>
                              {p.isStockShared && (
                                <span className="badge" title="Todas las presentaciones descuentan del stock centralizado del padre" style={{ backgroundColor: '#ECFDF5', color: '#047857', border: '1px solid #A7F3D0', fontSize: '0.72rem', padding: '2px 6px', borderRadius: '4px' }}>
                                  Stock Compartido
                                </span>
                              )}
                              {p.hasIndependentPricing && (
                                <span className="badge" title="Cada presentación define su costo y precio individual" style={{ backgroundColor: '#EFF6FF', color: '#1D4ED8', border: '1px solid #BFDBFE', fontSize: '0.72rem', padding: '2px 6px', borderRadius: '4px' }}>
                                  Precios Indep.
                                </span>
                              )}
                            </>
                          )}
                        </div>
                      </td>
                      <td style={{ padding: '14px', textAlign: 'right', whiteSpace: 'nowrap' }} className="font-mono font-bold" title={isIndepParent ? 'Precios individuales definidos en cada variante' : undefined}>
                        {displayRetail}
                      </td>

                      {/* Celdas dinámicas de Precio al Mayor */}
                      {showWholesale && (
                        <>
                          <td style={{ padding: '14px', textAlign: 'right', whiteSpace: 'nowrap', backgroundColor: 'rgba(99, 102, 241, 0.03)' }} className="font-bold font-mono" title={isIndepParent ? 'Precios individuales definidos en cada variante' : undefined}>
                            <span style={{ color: isIndepParent ? 'inherit' : wholesaleColor }}>
                              {displayWholesale}
                            </span>
                            {!isIndepParent && !hasRealWholesale && (
                              <span style={{ fontSize: '0.72rem', color: '#D97706', marginLeft: '5px', fontWeight: 500 }}>
                                (Detal)
                              </span>
                            )}
                          </td>
                          <td style={{ padding: '14px', textAlign: 'center', backgroundColor: 'rgba(99, 102, 241, 0.03)' }} className="font-bold">
                            <span style={{ color: (isIndepParent || hasRealWholesale) ? 'inherit' : '#D97706' }}>
                              {isIndepParent ? '—' : `${minQty} ${unitStr}`}
                            </span>
                          </td>
                        </>
                      )}

                      <td style={{ padding: '14px', textAlign: 'center' }}>
                        {p.isCashAdvance ? (
                          <span className="badge" style={{ backgroundColor: '#EDE9FE', color: '#6D28D9', border: '1px solid #DDD6FE', fontWeight: 600 }}>
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
                              <div style={{ fontSize: '0.68rem', color: 'var(--text-muted)', marginTop: '2px' }}>
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
                      <span className="badge" style={{ backgroundColor: '#EDE9FE', color: '#6D28D9', border: '1px solid #DDD6FE', fontSize: '0.8rem', padding: '4px 10px', borderRadius: '12px', fontWeight: 600 }}>
                        Servicio
                      </span>
                    ) : (
                      <span className={`badge ${stockQty > 0 ? 'badge-success' : 'badge-danger'}`} style={{ fontSize: '0.8rem', padding: '4px 10px', borderRadius: '12px' }}>
                        {p.isGroupHeader ? `Total: ${stockQty}` : `Stock: ${stockQty}`} {unitStr}
                      </span>
                    )}
                  </div>

                  {/* Renglón 2: PRODUCTO en texto grande y negrita */}
                  <div className="catalog-mobile-card-title" style={{ color: 'var(--text-primary)', fontSize: '1.05rem', fontWeight: 700 }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: '6px', flexWrap: 'wrap' }}>
                      <span>{p.name || p.productName || '-'}</span>
                      {p.isGroupHeader && (
                        <>
                          <span className="badge-variant-group">
                            {p.variantCount > 0 ? `${p.variantCount} variantes` : 'Grupo'}
                          </span>
                          {p.isStockShared && (
                            <span className="badge" style={{ backgroundColor: '#ECFDF5', color: '#047857', border: '1px solid #A7F3D0', fontSize: '0.72rem', padding: '2px 6px', borderRadius: '4px' }}>
                              Stock Compartido
                            </span>
                          )}
                          {p.hasIndependentPricing && (
                            <span className="badge" style={{ backgroundColor: '#EFF6FF', color: '#1D4ED8', border: '1px solid #BFDBFE', fontSize: '0.72rem', padding: '2px 6px', borderRadius: '4px' }}>
                              Precios Indep.
                            </span>
                          )}
                        </>
                      )}
                    </div>
                  </div>

                  {/* Renglón 3: PRECIO DETAL en Moneda seleccionada */}
                  <div className="catalog-mobile-card-prices">
                    <div style={{ display: 'flex', flexDirection: 'column' }}>
                      <span className="text-xs text-muted">Precio Detal ({currency})</span>
                      <span className="font-bold font-mono" style={{ whiteSpace: 'nowrap', fontSize: '1rem' }}>
                        {displayRetail}
                      </span>
                    </div>

                    <div style={{ textAlign: 'right', display: 'flex', flexDirection: 'column', alignItems: 'flex-end' }}>
                      <span className="text-xs text-muted">Stock Disponible</span>
                      <span className="font-bold" style={{ fontSize: '0.9rem' }}>
                        {stockQty} {unitStr}
                      </span>
                    </div>
                  </div>

                  {/* Renglón 4 (Opcional): PRECIO AL MAYOR Y CANTIDAD MÍNIMA en Móvil */}
                  {showWholesale && (
                    <div className="catalog-wholesale-card-box mt-2" style={{ borderColor: hasRealWholesale ? 'rgba(99, 102, 241, 0.2)' : 'rgba(217, 119, 6, 0.3)' }}>
                      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                        <div>
                          <div className="text-xs text-muted">
                            Precio al Mayor ({currency})
                            {!hasRealWholesale && <span style={{ color: '#D97706', marginLeft: '4px', fontWeight: 600 }}>(Sin Descuento)</span>}
                          </div>
                          <div className="font-bold font-mono" style={{ whiteSpace: 'nowrap', color: wholesaleColor, fontSize: '0.98rem' }}>
                            {displayWholesale}
                          </div>
                        </div>
                        <div style={{ textAlign: 'right' }}>
                          <div className="text-xs text-muted">Cant. Mínima</div>
                          <div className="font-bold" style={{ whiteSpace: 'nowrap', color: hasRealWholesale ? 'inherit' : '#D97706' }}>
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
            onPageChange={(p) => loadProducts(debouncedSearch, p)}
            loading={loading}
            itemLabel="productos"
          />
        </>
      )}
    </div>
  );
}
