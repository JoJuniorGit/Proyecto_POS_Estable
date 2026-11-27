import { useState, useEffect, useRef, forwardRef, useImperativeHandle } from 'react';
import { Search, X } from 'lucide-react';
import { getProductSuggestions } from '../../services/productsApi';
import SuggestionList from './SuggestionList';
import { useExchangeRate } from '../../context/ExchangeRateContext';
import { useDebounce } from '../../hooks/useDebounce';
import { shouldRefetchOnRateChange } from '../../utils/formatters';

/**
 * 8.186-T7 (S1, refactor H3): decisión del ÚNICO efecto de búsqueda para un commit de React.
 * El efecto corre una vez por commit cuando cambia [exchangeRate, debouncedQuery, categoryId];
 * esta función fusiona los dos efectos de 8.186-T2 en UNA acción: 'clear' sin búsqueda activa,
 * 'skip' cuando el cambio no amerita petición (transición inicial 0 → tasa o repeticiones, vía
 * shouldRefetchOnRateChange) y 'fetch' cuando cambió la consulta/categoría o la tasa real.
 * Tasa y consulta en el mismo commit producen un solo 'fetch' (elimina la duplicación H3).
 * @param {{rate: number, query: string, categoryId: (number|string|undefined)}} previousTrigger
 * @param {{rate: number, query: string, categoryId: (number|string|undefined)}} nextTrigger
 * @returns {'clear'|'skip'|'fetch'}
 */
export function resolveSuggestionFetchMode(previousTrigger, nextTrigger) {
  const previous = previousTrigger || {};
  const next = nextTrigger || {};
  if (!String(next.query || '').trim()) return 'clear';

  const queryOrCategoryChanged =
    previous.query !== next.query || previous.categoryId !== next.categoryId;
  if (queryOrCategoryChanged) return 'fetch';

  return shouldRefetchOnRateChange(previous.rate, next.rate) ? 'fetch' : 'skip';
}

const SearchBar = forwardRef(function SearchBar({ onSelectProduct, categoryId }, ref) {
  const [query, setQuery] = useState('');
  const [suggestions, setSuggestions] = useState([]);
  const [loading, setLoading] = useState(false);
  const [isOpen, setIsOpen] = useState(false);
  const searchRef = useRef(null);
  const inputRef = useRef(null);
  const { exchangeRate } = useExchangeRate();

  const debouncedQuery = useDebounce(query, 300);

  // 8.186-T7 (S1, H3): UN solo camino de fetch de sugerencias. El efecto corre una vez por commit
  // cuando cambia [exchangeRate, debouncedQuery, categoryId] y `resolveSuggestionFetchMode` decide:
  // consulta/categoría cambiadas → fetch; tasa real distinta (SignalR) → refetch para reproyectar el
  // efectivo; transición inicial 0 → tasa o repeticiones → skip (el fetch ya usó la proyección del
  // servidor); sin consulta activa → clear. Tasa y consulta en el mismo commit → un único fetch.
  const previousTriggerRef = useRef({ rate: exchangeRate, query: debouncedQuery, categoryId });
  const fetchControllerRef = useRef(null);

  useEffect(() => {
    const previousTrigger = previousTriggerRef.current;
    const nextTrigger = { rate: exchangeRate, query: debouncedQuery, categoryId };
    previousTriggerRef.current = nextTrigger;

    const mode = resolveSuggestionFetchMode(previousTrigger, nextTrigger);

    if (mode === 'clear') {
      fetchControllerRef.current?.abort();
      fetchControllerRef.current = null;
      setSuggestions([]);
      setIsOpen(false);
      setLoading(false);
      return;
    }
    if (mode === 'skip') return;

    // Cada nuevo disparo aborta la petición anterior; sólo la petición vigente cierra el loading.
    fetchControllerRef.current?.abort();
    const controller = new AbortController();
    fetchControllerRef.current = controller;
    setLoading(true);

    getProductSuggestions(debouncedQuery, controller.signal, categoryId)
      .then((results) => {
        if (controller.signal.aborted) return;
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
        if (!controller.signal.aborted) setLoading(false);
      });
  }, [exchangeRate, debouncedQuery, categoryId]);

  // El fetch en vuelo se aborta al desmontar (abortar sin petición es no-op, StrictMode-safe).
  useEffect(() => () => {
    fetchControllerRef.current?.abort();
    fetchControllerRef.current = null;
  }, []);

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
