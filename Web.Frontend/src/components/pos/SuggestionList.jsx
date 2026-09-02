import { Search, Loader2, X } from 'lucide-react';

export default function SuggestionList({ suggestions, isLoading, onSelectSuggestion, onClose, exchangeRate }) {
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
        const priceBsS = item.priceUSD > 0 ? item.priceUSD * (exchangeRate || 1) : (item.priceBsS || 0);
        return (
          <div
            key={item.id}
            className="search-suggestion-item"
            onClick={() => onSelectSuggestion(item)}
          >
            <div className="suggestion-info">
              <div className="flex-center-gap" style={{ justifyContent: 'flex-start', flexWrap: 'wrap', gap: '6px' }}>
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
                {item.isGroupHeader && item.hasIndependentPricing ? 'Precios indiv.' : `Bs.S ${priceBsS.toFixed(2)}`}
              </span>
              <span 
                className={`suggestion-stock ${item.isCashAdvance ? 'service' : ((item.consolidatedStock ?? item.stockQuantity) <= 0 ? 'out' : '')}`}
                style={item.isCashAdvance ? { backgroundColor: '#EDE9FE', color: '#6D28D9', border: '1px solid #DDD6FE', padding: '2px 8px', borderRadius: '8px', fontWeight: 600 } : undefined}
              >
                {item.isCashAdvance
                  ? 'Servicio'
                  : (item.isGroupHeader
                      ? `Stock total: ${item.consolidatedStock ?? 0}`
                      : `Disponibles: ${item.availableQuantity ?? item.stockQuantity ?? 0}`)}
              </span>
            </div>
          </div>
        );
      })}
    </div>
  );
}
