import { useState, useEffect, useCallback } from 'react';
import { api } from '../services/api';
import { Package, Search, Loader2, RefreshCw, ChevronLeft, ChevronRight, Tag, DollarSign } from 'lucide-react';
import { useExchangeRate } from '../context/ExchangeRateContext';
import { formatBsS, formatUSD } from '../utils/formatters';

export default function CatalogPage() {
  const { exchangeRate } = useExchangeRate();
  const [products, setProducts] = useState([]);
  const [loading, setLoading] = useState(false);
  const [search, setSearch] = useState('');
  
  // Filtro de Moneda: "Bs.S" por defecto al entrar al catálogo
  const [currency, setCurrency] = useState('Bs.S');

  // Botón desactivado por defecto al entrar al catálogo
  const [showWholesale, setShowWholesale] = useState(false);

  // Paginación de 25 elementos por página
  const [currentPage, setCurrentPage] = useState(1);
  const [totalCount, setTotalCount] = useState(0);
  const [totalPages, setTotalPages] = useState(1);
  const pageSize = 25;

  const loadProducts = useCallback(async (filter = '', page = 1) => {
    setLoading(true);
    try {
      const filterParam = filter ? `&filter=${encodeURIComponent(filter)}` : '';
      const data = await api.get(`/api/products?page=${page}&pageSize=${pageSize}${filterParam}`);
      
      const items = data?.items || (Array.isArray(data) ? data : []);
      const total = data?.totalCount ?? items.length;
      const pages = data?.totalPages ?? (Math.ceil(total / pageSize) || 1);

      setProducts(items);
      setTotalCount(total);
      setTotalPages(pages);
      setCurrentPage(page);
    } catch (err) {
      console.error('[CatalogPage] Error cargando catálogo:', err);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadProducts(search, 1);
  }, []);

  const handleSearchChange = (e) => {
    const val = e.target.value;
    setSearch(val);
    loadProducts(val, 1);
  };

  const handleSearchSubmit = (e) => {
    e.preventDefault();
    loadProducts(search, 1);
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
        <div className="p-5 text-center text-muted" style={{ padding: '60px', textAlign: 'center' }}>
          <Loader2 className="animate-spin mb-2 mx-auto" size={32} />
          <div>Cargando catálogo de productos...</div>
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
                  <th style={{ padding: '14px', width: '120px' }}>SKU</th>
                  <th style={{ padding: '14px' }}>Producto</th>
                  <th style={{ padding: '14px', textAlign: 'right', whiteSpace: 'nowrap' }}>
                    Precio Detal ({currency})
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

                  <th style={{ padding: '14px', textAlign: 'center', width: '100px' }}>Stock</th>
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

                  const stockQty = p.stockQuantity ?? p.stock ?? 0;
                  const unitStr = p.unitOfMeasureStr || (p.unitOfMeasure !== undefined && p.unitOfMeasure !== 0 ? p.unitOfMeasure : 'Und');

                  // Formato según Moneda seleccionada
                  const displayRetail = currency === 'USD' ? formatUSD(retailUSD) : formatBsS(retailBsS);
                  const displayWholesale = currency === 'USD' ? formatUSD(wholesaleUSD) : formatBsS(wholesaleBsS);

                  return (
                    <tr key={p.id} style={{ borderBottom: '1px solid var(--border-color)' }}>
                      <td style={{ padding: '14px' }} className="font-mono text-muted">{p.sku || '-'}</td>
                      <td style={{ padding: '14px' }} className="font-medium">{p.name}</td>
                      <td style={{ padding: '14px', textAlign: 'right', whiteSpace: 'nowrap' }} className="font-mono font-bold">
                        {displayRetail}
                      </td>

                      {/* Celdas dinámicas de Precio al Mayor */}
                      {showWholesale && (
                        <>
                          <td style={{ padding: '14px', textAlign: 'right', whiteSpace: 'nowrap', backgroundColor: 'rgba(99, 102, 241, 0.03)' }} className="font-bold font-mono">
                            <span style={{ color: wholesaleColor }}>
                              {displayWholesale}
                            </span>
                            {!hasRealWholesale && (
                              <span style={{ fontSize: '0.72rem', color: '#D97706', marginLeft: '5px', fontWeight: 500 }}>
                                (Detal)
                              </span>
                            )}
                          </td>
                          <td style={{ padding: '14px', textAlign: 'center', backgroundColor: 'rgba(99, 102, 241, 0.03)' }} className="font-bold">
                            <span style={{ color: hasRealWholesale ? 'inherit' : '#D97706' }}>
                              {minQty} {unitStr}
                            </span>
                          </td>
                        </>
                      )}

                      <td style={{ padding: '14px', textAlign: 'center' }}>
                        <span className={`badge ${stockQty > 0 ? 'badge-success' : 'badge-danger'}`}>
                          {stockQty} {unitStr !== 'Und' ? unitStr : ''}
                        </span>
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

              const stockQty = p.stockQuantity ?? p.stock ?? 0;
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
                    <span className={`badge ${stockQty > 0 ? 'badge-success' : 'badge-danger'}`} style={{ fontSize: '0.8rem', padding: '4px 10px', borderRadius: '12px' }}>
                      Stock: {stockQty} {unitStr}
                    </span>
                  </div>

                  {/* Renglón 2: PRODUCTO en texto grande y negrita */}
                  <div className="catalog-mobile-card-title" style={{ color: 'var(--text-primary)', fontSize: '1.05rem', fontWeight: 700 }}>
                    {p.name || p.productName || '-'}
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

          {/* ── 5. BARRA DE PAGINACIÓN DE 25 ELEMENTOS (Ambas Versiones) ── */}
          <div className="catalog-pagination-bar card p-3">
            <button
              type="button"
              className="btn btn-outline btn-sm flex-align-center gap-1"
              onClick={handlePrevPage}
              disabled={currentPage <= 1 || loading}
              style={{ display: 'inline-flex', alignItems: 'center', gap: '4px' }}
            >
              <ChevronLeft size={16} /> Anterior
            </button>

            <span className="font-medium text-sm text-center" style={{ padding: '0 8px' }}>
              Página <strong>{currentPage}</strong> de <strong>{totalPages}</strong> <span className="text-muted">({totalCount} productos)</span>
            </span>

            <button
              type="button"
              className="btn btn-outline btn-sm flex-align-center gap-1"
              onClick={handleNextPage}
              disabled={currentPage >= totalPages || loading}
              style={{ display: 'inline-flex', alignItems: 'center', gap: '4px' }}
            >
              Siguiente <ChevronRight size={16} />
            </button>
          </div>
        </>
      )}
    </div>
  );
}
