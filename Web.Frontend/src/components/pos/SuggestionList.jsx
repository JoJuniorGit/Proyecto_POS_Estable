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
              <span className="suggestion-name">{item.name}</span>
              <span className="suggestion-sku">SKU: {item.sku || '-'}</span>
            </div>
            <div className="suggestion-price-stock">
              <span className="suggestion-price">Bs.S {priceBsS.toFixed(2)}</span>
              <span className={`suggestion-stock ${item.stockQuantity <= 0 ? 'out' : ''}`}>
                Disponibles: {item.availableQuantity ?? item.stockQuantity ?? 0}
              </span>
            </div>
          </div>
        );
      })}
    </div>
  );
}
