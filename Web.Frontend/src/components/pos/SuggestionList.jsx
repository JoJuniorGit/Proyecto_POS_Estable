import { Loader2 } from 'lucide-react';
import { formatNumberEs, resolveBsSPrice, resolveEffectiveBsS } from '../../utils/formatters';
import { useMonetaryProfile } from '../../context/MonetaryProfileContext';
import { MONETARY_MODE_DUAL } from '../../utils/monetaryProfile';
import './SuggestionList.css';

export default function SuggestionList({ suggestions, isLoading, onSelectSuggestion, exchangeRate }) {
  const { mode, primaryCurrencySymbol, secondaryCurrencySymbol } = useMonetaryProfile();
  const localCurrencySymbol = (mode === MONETARY_MODE_DUAL && secondaryCurrencySymbol)
    || primaryCurrencySymbol;

  if (isLoading) {
    return (
      <div className="search-dropdown flex-center">
        <Loader2 className="animate-spin" size={20} />
        <span>Buscando productos...</span>
      </div>
    );
  }

  if (!suggestions || suggestions.length === 0) {
    return (
      <div className="search-dropdown empty">
        <span>Producto no encontrado</span>
      </div>
    );
  }

  return (
    <div className="search-dropdown">
      {suggestions.map((item) => {
        // 8.186-T2 (S1/S3): el servidor proyecta el efectivo Bs.S (motor único); el canónico
        // USD × tasa (8.143) queda solo como fallback documentado si el campo falta o viene <= 0.
        const priceBsS = resolveEffectiveBsS(
          item.effectivePriceBsS,
          resolveBsSPrice(item.priceUSD, item.priceBsS, exchangeRate)
        );
        return (
          <div
            key={item.id}
            className="search-suggestion-item"
            onClick={() => onSelectSuggestion(item)}
          >
            <div className="suggestion-info">
              <div className="flex-center-gap sl-tags-row">
                <span className="suggestion-name">{item.name}</span>
                {item.isGroupHeader && (
                  <span className="badge-variant-group">
                    {item.variantCount > 0 ? `${item.variantCount} variantes` : 'Grupo'}
                  </span>
                )}
              </div>
              <span className="suggestion-sku">SKU: {item.sku || '-'}</span>
            </div>
            <div className="suggestion-price-stock">
              <span 
                className="suggestion-price"
                title={item.isGroupHeader && item.hasIndependentPricing ? 'Este producto agrupador posee costos y precios individuales por variante' : undefined}
              >
                {item.isGroupHeader && item.hasIndependentPricing ? (
                  'Precios indiv.'
                ) : (
                  <>
                    <span className="price-currency-symbol">{localCurrencySymbol} </span>
                    {formatNumberEs(priceBsS)}
                  </>
                )}
              </span>
              <span 
                className={`suggestion-stock ${
                  item.isCashAdvance 
                    ? 'service' 
                    : (item.isGroupHeader && !item.isStockShared)
                      ? 'na'
                      : ((item.consolidatedStock ?? item.stockQuantity) <= 0 ? 'out' : '')
                }`}
                style={item.isCashAdvance ? { backgroundColor: '#EDE9FE', color: '#6D28D9', border: '1px solid #DDD6FE', padding: '2px 8px', borderRadius: '8px', fontWeight: 600 } : undefined}
                title={item.isGroupHeader && !item.isStockShared ? 'El inventario reside individualmente en cada variante' : undefined}
              >
                {item.isCashAdvance
                  ? 'Servicio'
                  : (item.isGroupHeader
                      ? (item.isStockShared ? `Stock total: ${item.consolidatedStock ?? item.stockQuantity ?? 0}` : 'Stock: N/A')
                      : `Disponibles: ${item.availableQuantity ?? item.stockQuantity ?? 0}`)}
              </span>
            </div>
          </div>
        );
      })}
    </div>
  );
}
