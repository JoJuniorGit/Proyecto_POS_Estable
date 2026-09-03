import { useState, useEffect } from 'react';
import { X, Loader2, Package, Check, AlertCircle, Link2 } from 'lucide-react';
import { api } from '../../services/api';
import { formatUSD } from '../../utils/formatters';

function formatVariantTitle(variantName, parentName) {
  if (!variantName) return '';
  if (!parentName) return variantName;

  const pTrim = parentName.trim();
  const vTrim = variantName.trim();
  const pLower = pTrim.toLowerCase();
  const vLower = vTrim.toLowerCase();

  // 1. Eliminación de prefijo directo idéntico (case-insensitive)
  if (vLower.startsWith(pLower)) {
    const remainder = vTrim.slice(pTrim.length).replace(/^[\s\-–—/:]+/, '').trim();
    if (remainder.length >= 2) return remainder;
  }

  // 2. Eliminación de palabras redundantes compartidas con el padre al inicio
  const parentWords = pLower.split(/\s+/).filter((w) => w.length > 2);
  const words = vTrim.split(/\s+/);
  let startIdx = 0;
  while (startIdx < words.length - 1) {
    const wLower = words[startIdx].toLowerCase();
    if (parentWords.includes(wLower) || ['bebida', 'refresco', 'galleta', 'producto'].includes(wLower)) {
      startIdx++;
    } else {
      break;
    }
  }

  if (startIdx > 0) {
    const simplified = words.slice(startIdx).join(' ').replace(/^[\s\-–—/:]+/, '').trim();
    if (simplified.length >= 2) return simplified;
  }

  return variantName;
}

export default function VariantSelectorModal({ isOpen, onClose, parentProduct, onSelectVariant, exchangeRate }) {
  const [variants, setVariants] = useState([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    if (!isOpen || !parentProduct?.id) {
      setVariants([]);
      setError('');
      return;
    }

    let isMounted = true;
    const fetchVariants = async () => {
      setLoading(true);
      setError('');
      try {
        const data = await api.get(`/api/products/${parentProduct.id}/variants`);
        if (isMounted) {
          setVariants(Array.isArray(data) ? data : []);
        }
      } catch (err) {
        if (isMounted) {
          console.error('[VariantSelectorModal] Error al obtener variantes:', err);
          setError('No se pudieron cargar las variantes del producto.');
        }
      } finally {
        if (isMounted) {
          setLoading(false);
        }
      }
    };

    fetchVariants();

    return () => {
      isMounted = false;
    };
  }, [isOpen, parentProduct]);

  if (!isOpen || !parentProduct) return null;

  const basePriceUSD = parentProduct.priceRetailUSD || parentProduct.priceUSD || 0;

  const handleSelect = (variant) => {
    onSelectVariant(variant);
    onClose();
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal-content variant-selector-modal" onClick={(e) => e.stopPropagation()}>
        <div className="modal-header variant-modal-header">
          <div className="variant-modal-title-box">
            <div className="variant-modal-title-row">
              <div className="variant-header-icon-wrap">
                <Package size={20} className="variant-header-icon" />
              </div>
              <h3>{parentProduct.name}</h3>
            </div>
          </div>
          <button className="btn-close" onClick={onClose} aria-label="Cerrar modal">
            <X size={20} />
          </button>
        </div>

        <div className="modal-body variant-selector-body">
          <p className="variant-modal-subtitle">
            Selecciona el sabor o presentación deseada para agregar a la venta:
          </p>

          {loading && (
            <div className="flex-center" style={{ padding: '2.5rem 0', flexDirection: 'column', gap: '10px' }}>
              <Loader2 className="animate-spin color-primary" size={32} />
              <span className="text-muted">Cargando sabores y presentaciones...</span>
            </div>
          )}

          {error && (
            <div className="alert-error flex-center-gap" style={{ padding: '12px', borderRadius: '8px', marginBottom: '12px' }}>
              <AlertCircle size={20} />
              <span>{error}</span>
            </div>
          )}

          {!loading && !error && variants.length === 0 && (
            <div className="empty-state" style={{ padding: '2.5rem 0', textAlign: 'center' }}>
              <p className="text-muted">Este producto no tiene presentaciones activas registradas.</p>
            </div>
          )}

          {!loading && variants.length > 0 && (
            <div className="variant-grid">
              {variants.map((v) => {
                const isOutOfStock = (v.availableQuantity ?? v.stockQuantity) <= 0;
                const displayName = formatVariantTitle(v.name, parentProduct.name);
                return (
                  <button
                    key={v.id}
                    type="button"
                    className={`variant-card ${isOutOfStock ? 'out-of-stock' : ''}`}
                    onClick={() => handleSelect(v)}
                    disabled={isOutOfStock}
                  >
                    <div className="variant-card-header">
                      <span className="variant-name" title={v.name}>{displayName}</span>
                      <div className="variant-card-meta">
                        <span className="variant-sku">SKU: {v.sku || '-'}</span>
                      </div>
                      <span className="variant-card-price">
                        {formatUSD(v.priceRetailUSD || basePriceUSD)}
                      </span>
                    </div>
                    <div className="variant-card-footer">
                      <span 
                        className={`variant-stock-badge ${isOutOfStock ? 'out' : ''}`}
                        title={v.isStockShared ? 'Inventario compartido' : undefined}
                      >
                        {v.isStockShared && <Link2 size={12} className="variant-stock-icon" />}
                        {isOutOfStock ? 'Agotado' : `${v.availableQuantity ?? v.stockQuantity} disponibles`}
                      </span>
                      <span className="variant-select-cta">
                        <Check size={16} /> Seleccionar
                      </span>
                    </div>
                  </button>
                );
              })}
            </div>
          )}
        </div>

        <div className="modal-footer variant-modal-footer">
          <button type="button" className="variant-btn-cancel" onClick={onClose}>
            Cancelar
          </button>
        </div>
      </div>
    </div>
  );
}
