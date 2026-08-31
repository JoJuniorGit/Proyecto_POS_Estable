import { useState, useEffect } from 'react';
import { X, Loader2, Package, Check, AlertCircle } from 'lucide-react';
import { api } from '../../services/api';
import { formatBsS, formatUSD } from '../../utils/formatters';

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
  const basePriceBsS = parentProduct.priceBsS || (basePriceUSD * (exchangeRate || 1));

  const handleSelect = (variant) => {
    onSelectVariant(variant);
    onClose();
  };

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal-content variant-selector-modal" onClick={(e) => e.stopPropagation()}>
        <div className="modal-header">
          <div className="variant-modal-title-box">
            <div className="flex-center-gap">
              <Package size={22} className="color-primary" />
              <h3>{parentProduct.name}</h3>
            </div>
            <div className="variant-modal-price-tag">
              <span className="price-usd">{formatUSD(basePriceUSD)}</span>
              <span className="price-bss">({formatBsS(basePriceBsS)})</span>
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
            <div className="flex-center" style={{ padding: '2rem 0', flexDirection: 'column', gap: '8px' }}>
              <Loader2 className="animate-spin color-primary" size={32} />
              <span className="text-muted">Cargando sabores y variantes...</span>
            </div>
          )}

          {error && (
            <div className="alert-error flex-center-gap" style={{ padding: '12px', borderRadius: '8px', marginBottom: '12px' }}>
              <AlertCircle size={20} />
              <span>{error}</span>
            </div>
          )}

          {!loading && !error && variants.length === 0 && (
            <div className="empty-state" style={{ padding: '2rem 0', textAlign: 'center' }}>
              <p className="text-muted">Este producto no tiene variantes activas registradas.</p>
            </div>
          )}

          {!loading && variants.length > 0 && (
            <div className="variant-grid">
              {variants.map((v) => {
                const isOutOfStock = (v.availableQuantity ?? v.stockQuantity) <= 0;
                return (
                  <button
                    key={v.id}
                    className={`variant-card ${isOutOfStock ? 'out-of-stock' : ''}`}
                    onClick={() => handleSelect(v)}
                    disabled={isOutOfStock}
                  >
                    <div className="variant-card-header">
                      <span className="variant-name">{v.name}</span>
                      <span className="variant-sku">SKU: {v.sku}</span>
                    </div>
                    <div className="variant-card-footer">
                      <span className={`variant-stock-badge ${isOutOfStock ? 'out' : ''}`}>
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

        <div className="modal-footer">
          <button className="btn btn-secondary" onClick={onClose}>
            Cancelar
          </button>
        </div>
      </div>
    </div>
  );
}
