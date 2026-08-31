import { useState, useEffect, useRef, forwardRef, useImperativeHandle } from 'react';
import { Search, X } from 'lucide-react';
import { getProductSuggestions } from '../../services/productsApi';
import SuggestionList from './SuggestionList';
import { useExchangeRate } from '../../context/ExchangeRateContext';
import { useDebounce } from '../../hooks/useDebounce';

const SearchBar = forwardRef(function SearchBar({ onSelectProduct }, ref) {
  const [query, setQuery] = useState('');
  const [suggestions, setSuggestions] = useState([]);
  const [loading, setLoading] = useState(false);
  const [isOpen, setIsOpen] = useState(false);
  const searchRef = useRef(null);
  const inputRef = useRef(null);
  const { exchangeRate } = useExchangeRate();

  const debouncedQuery = useDebounce(query, 300);

  // Permite que la página POS limpie el buscador o le dé foco con F2.
  useImperativeHandle(ref, () => ({
    setQuery: (text) => {
      setQuery(text);
    },
    focus: () => {
      inputRef.current?.focus();
      inputRef.current?.select();
    },
    clear: () => {
      setQuery('');
      setSuggestions([]);
      setIsOpen(false);
    },
  }));

  // Cancelar peticiones fetch en vuelo al cambiar la búsqueda debounced
  useEffect(() => {
    if (!debouncedQuery.trim()) {
      setSuggestions([]);
      setIsOpen(false);
      return;
    }

    const controller = new AbortController();
    setLoading(true);

    getProductSuggestions(debouncedQuery, controller.signal)
      .then((results) => {
        setSuggestions(results || []);
        setIsOpen(true);
      })
      .catch((err) => {
        if (err.name !== 'AbortError') {
          console.error('[SearchBar] Error en búsqueda:', err);
          setSuggestions([]);
        }
      })
      .finally(() => {
        setLoading(false);
      });

    return () => {
      controller.abort();
    };
  }, [debouncedQuery]);

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

    // Re-enfocar el campo de búsqueda inmediatamente sólo si no hay un modal abriéndose/abierto
    if (!document.querySelector('.modal-backdrop, .modal, [role="dialog"]')) {
      inputRef.current?.focus();
    }
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
          ref={inputRef}
          type="text"
          className="search-input"
          placeholder="Buscar producto por nombre o código (F2)..."
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
