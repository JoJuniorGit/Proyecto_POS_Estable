import { useState, useEffect, useRef, forwardRef, useImperativeHandle } from 'react';
import { Search, X } from 'lucide-react';
import { getProductSuggestions } from '../../services/productsApi';
import SuggestionList from './SuggestionList';
import { useExchangeRate } from '../../context/ExchangeRateContext';

const SearchBar = forwardRef(function SearchBar({ onSelectProduct }, ref) {
  const [query, setQuery] = useState('');
  const [suggestions, setSuggestions] = useState([]);
  const [loading, setLoading] = useState(false);
  const [isOpen, setIsOpen] = useState(false);
  const searchRef = useRef(null);
  const { exchangeRate } = useExchangeRate();

  // Permite que el escáner de código de barras deje un código no encontrado en la búsqueda.
  useImperativeHandle(ref, () => ({
    setQuery: (text) => {
      setQuery(text);
    },
  }));

  // Debounce search API calls
  useEffect(() => {
    if (!query.trim()) {
      setSuggestions([]);
      setIsOpen(false);
      return;
    }

    const controller = new AbortController();
    const timer = setTimeout(async () => {
      setLoading(true);
      try {
        const results = await getProductSuggestions(query, controller.signal);
        setSuggestions(results || []);
        setIsOpen(true);
      } catch (err) {
        if (err.name !== 'AbortError') {
          console.error('[SearchBar] Error en búsqueda:', err);
          setSuggestions([]);
        }
      } finally {
        setLoading(false);
      }
    }, 300);

    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [query]);

  // Handle outside click to close dropdown
  useEffect(() => {
    function handleClickOutside(event) {
      if (searchRef.current && !searchRef.current.contains(event.target)) {
        setIsOpen(false);
      }
    }
    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, []);

  const handleSelect = (product) => {
    onSelectProduct(product);
    setQuery('');
    setSuggestions([]);
    setIsOpen(false);
  };

  const handleKeyDown = (e) => {
    if (e.key === 'Escape') {
      setIsOpen(false);
    } else if (e.key === 'Enter' && suggestions.length > 0) {
      handleSelect(suggestions[0]);
    }
  };

  return (
    <div className="search-bar-container" ref={searchRef}>
      <div className="search-input-wrapper">
        <Search className="search-icon" size={18} />
        <input
          type="text"
          className="search-input"
          placeholder="Buscar producto por nombre o código (SKU)..."
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onFocus={() => query.trim() && setIsOpen(true)}
          onKeyDown={handleKeyDown}
        />
        {query && (
          <button
            type="button"
            className="search-clear-btn"
            onClick={() => {
              setQuery('');
              setSuggestions([]);
              setIsOpen(false);
            }}
          >
            <X size={16} />
          </button>
        )}
      </div>

      {isOpen && (
        <SuggestionList
          suggestions={suggestions}
          isLoading={loading}
          onSelectSuggestion={handleSelect}
          onClose={() => setIsOpen(false)}
          exchangeRate={exchangeRate}
        />
      )}
    </div>
  );
});

export default SearchBar;
