import { useState, useEffect, useRef, forwardRef, useImperativeHandle } from 'react';
import { Search, X } from 'lucide-react';
import { getProductSuggestions } from '../../services/productsApi';
import SuggestionList from './SuggestionList';
import { useExchangeRate } from '../../context/ExchangeRateContext';
import { useDebounce } from '../../hooks/useDebounce';
import { shouldRefetchOnRateChange } from '../../utils/formatters';

const SearchBar = forwardRef(function SearchBar({ onSelectProduct, categoryId }, ref) {
  const [query, setQuery] = useState('');
  const [suggestions, setSuggestions] = useState([]);
  const [loading, setLoading] = useState(false);
  const [isOpen, setIsOpen] = useState(false);
  const searchRef = useRef(null);
  const inputRef = useRef(null);
  const { exchangeRate } = useExchangeRate();

  const debouncedQuery = useDebounce(query, 300);

  // 8.186-T2 (S1): la tasa vigente llega por SignalR; al cambiar, la búsqueda activa se relanza
  // para que el servidor reproyecte el efectivo. La transición inicial 0 → tasa no refetchea (el
  // fetch de montaje ya usó la tasa del servidor) y los valores repetidos no disparan loops.
  const previousRateRef = useRef(exchangeRate);
  const rateFetchRef = useRef(null);
  useEffect(() => {
    const previousRate = previousRateRef.current;
    previousRateRef.current = exchangeRate;

    if (!shouldRefetchOnRateChange(previousRate, exchangeRate)) {
      // La consulta cambió (el efecto principal ya refetchea) o la tasa no cambió: descarta el
      // refetch de tasa en vuelo de OTRA consulta para no pisar resultados frescos.
      if (rateFetchRef.current && rateFetchRef.current.queryText !== debouncedQuery) {
        rateFetchRef.current.controller.abort();
        rateFetchRef.current = null;
      }
      return;
    }

    const queryText = debouncedQuery;
    if (!queryText.trim()) return;

    rateFetchRef.current?.controller.abort();
    const controller = new AbortController();
    rateFetchRef.current = { controller, queryText };
    setLoading(true);

    getProductSuggestions(queryText, controller.signal, categoryId)
      .then((results) => {
        if (controller.signal.aborted) return;
        setSuggestions(results || []);
        setIsOpen(true);
      })
      .catch((err) => {
        if (err.name !== 'AbortError') {
          console.error('[SearchBar] Error en búsqueda:', err);
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
  }, [exchangeRate, debouncedQuery, categoryId]);

  // El refetch por tasa se aborta al desmontar (mismo patrón AbortController usado arriba).
  useEffect(() => () => rateFetchRef.current?.controller.abort(), []);

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

  // Cancelar peticiones fetch en vuelo al cambiar la búsqueda debounced o la categoría
  useEffect(() => {
    if (!debouncedQuery.trim()) {
      setSuggestions([]);
      setIsOpen(false);
      return;
    }

    const controller = new AbortController();
    setLoading(true);

    getProductSuggestions(debouncedQuery, controller.signal, categoryId)
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
  }, [debouncedQuery, categoryId]);

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
          className="search-input pos-search-input"
          data-scanner-trap="true"
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
