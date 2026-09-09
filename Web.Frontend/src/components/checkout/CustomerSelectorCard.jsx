import { useState, useEffect, useRef } from 'react';
import { User, Search, UserPlus, X, ChevronDown, ChevronUp, Loader2, Check } from 'lucide-react';
import { getCustomers, createCustomer } from '../../services/customerApi';
import useDebounce from '../../hooks/useDebounce';
import './CustomerSelectorCard.css';

export default function CustomerSelectorCard({
  currentCustomer,
  isPendingPickup = false,
  forceExpand = false,
  onSelectCustomer,
  disabled = false,
  readOnly = false
}) {
  const [isExpanded, setIsExpanded] = useState(false);
  const [query, setQuery] = useState('');
  const debouncedQuery = useDebounce(query, 300);
  const [customers, setCustomers] = useState([]);
  const [loadingCustomers, setLoadingCustomers] = useState(false);
  const [isDropdownOpen, setIsDropdownOpen] = useState(false);

  // Form para crear cliente nuevo
  const [isCreatingCustomer, setIsCreatingCustomer] = useState(false);
  const [newCustomer, setNewCustomer] = useState({ cedulaOrRif: '', name: '', phone: '' });

  const [saving, setSaving] = useState(false);
  const [error, setError] = useState(null);

  const searchInputRef = useRef(null);
  const containerRef = useRef(null);
  const blurTimeoutRef = useRef(null);

  const custName = (currentCustomer?.name || currentCustomer?.customerName || '').toLowerCase();
  const isDefaultCust = !currentCustomer?.id || 
    currentCustomer?.isDefault || 
    currentCustomer?.cedulaOrRif === 'V-00000000' ||
    custName.includes('consumidor final') || 
    custName.includes('general');

  // Cancelar y guardar/plegar automáticamente la lista de clientes
  const handleCancelAndCollapse = () => {
    setIsDropdownOpen(false);
    setIsExpanded(false);
    setIsCreatingCustomer(false);
    setQuery('');
    setError(null);
  };

  // Auto-expandir cuando se marca Mercancía en Custodia y el cliente es Consumidor Final
  useEffect(() => {
    if (!readOnly && (forceExpand || (isPendingPickup && isDefaultCust))) {
      setIsExpanded(true);
      setIsDropdownOpen(true);
      loadCustomers('');
      setTimeout(() => {
        if (searchInputRef.current) searchInputRef.current.focus();
      }, 80);
    }
  }, [forceExpand, isPendingPickup, isDefaultCust, readOnly]);

  // Manejar tecla Escape para cerrar dropdown y cancelar cambio
  useEffect(() => {
    function handleKeyDown(e) {
      if (e.key === 'Escape' && isExpanded) {
        handleCancelAndCollapse();
      }
    }
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isExpanded]);

  // Cerrar y cancelar al hacer clic o tap fuera del contenedor
  useEffect(() => {
    function handleClickOutside(event) {
      if (containerRef.current && !containerRef.current.contains(event.target)) {
        if (isExpanded && !saving) {
          handleCancelAndCollapse();
        }
      }
    }

    document.addEventListener('mousedown', handleClickOutside);
    document.addEventListener('touchstart', handleClickOutside);
    return () => {
      document.removeEventListener('mousedown', handleClickOutside);
      document.removeEventListener('touchstart', handleClickOutside);
    };
  }, [isExpanded, saving]);

  // Limpiar timer de blur al desmontar
  useEffect(() => {
    return () => {
      if (blurTimeoutRef.current) clearTimeout(blurTimeoutRef.current);
    };
  }, []);

  const loadCustomers = async (q) => {
    setLoadingCustomers(true);
    try {
      const data = await getCustomers(q);
      setCustomers((data || []).filter(c => !c.isDefault && c.cedulaOrRif !== 'V-00000000'));
    } catch (err) {
      console.error('[CustomerSelectorCard] Error al buscar clientes:', err);
    } finally {
      setLoadingCustomers(false);
    }
  };

  useEffect(() => {
    if (isExpanded) {
      loadCustomers(debouncedQuery);
    }
  }, [debouncedQuery, isExpanded]);

  const handleToggleExpand = () => {
    if (disabled || readOnly) return;
    const nextState = !isExpanded;
    setIsExpanded(nextState);
    setError(null);
    if (nextState) {
      setIsDropdownOpen(true);
      loadCustomers(query);
      setTimeout(() => {
        if (searchInputRef.current) searchInputRef.current.focus();
      }, 80);
    } else {
      setIsDropdownOpen(false);
      setIsCreatingCustomer(false);
    }
  };

  const handleSearchChange = (e) => {
    const val = e.target.value;
    setQuery(val);
    setIsDropdownOpen(true);
  };

  const handleInputFocus = () => {
    if (blurTimeoutRef.current) {
      clearTimeout(blurTimeoutRef.current);
    }
    setIsDropdownOpen(true);
  };

  const handleInputBlur = (e) => {
    const nextTarget = e.relatedTarget;
    // Si el foco se movió a otro elemento dentro del contenedor (ej. scrollbar o botón crear)
    if (containerRef.current && nextTarget && containerRef.current.contains(nextTarget)) {
      return;
    }

    // Al perder el foco del buscador, guardar/ocultar automáticamente la lista y cancelar el cambio de clientes
    blurTimeoutRef.current = setTimeout(() => {
      if (containerRef.current && containerRef.current.contains(document.activeElement)) {
        return;
      }
      handleCancelAndCollapse();
    }, 180);
  };

  const handleChooseCustomer = async (cust) => {
    if (!onSelectCustomer || disabled) return;
    setSaving(true);
    setError(null);
    try {
      await onSelectCustomer(cust);
      setIsExpanded(false);
      setIsDropdownOpen(false);
      setIsCreatingCustomer(false);
      setQuery('');
    } catch (err) {
      console.error('[CustomerSelectorCard] Error al seleccionar cliente:', err);
      const msg = err.response?.data?.message || err.response?.data || err.message || 'Error al asignar el cliente a la venta';
      setError(typeof msg === 'string' ? msg : 'Error al asignar el cliente.');
    } finally {
      setSaving(false);
    }
  };

  const handleCreateCustomerSubmit = async (e) => {
    e.preventDefault();
    if (!newCustomer.cedulaOrRif.trim() || !newCustomer.name.trim()) {
      setError('Cédula/RIF y Nombre completo son obligatorios.');
      return;
    }
    setSaving(true);
    setError(null);
    try {
      const created = await createCustomer({
        cedulaOrRif: newCustomer.cedulaOrRif.trim(),
        name: newCustomer.name.trim(),
        phone: newCustomer.phone.trim() || undefined
      });
      await onSelectCustomer(created);
      setIsCreatingCustomer(false);
      setIsExpanded(false);
      setIsDropdownOpen(false);
      setNewCustomer({ cedulaOrRif: '', name: '', phone: '' });
      setQuery('');
    } catch (err) {
      console.error('[CustomerSelectorCard] Error al crear cliente:', err);
      const msg = err.response?.data?.message || err.response?.data || err.message || 'Error al registrar nuevo cliente.';
      setError(typeof msg === 'string' ? msg : 'Error al registrar nuevo cliente. Verifique la Cédula/RIF.');
    } finally {
      setSaving(false);
    }
  };

  const isWarningHighlight = isPendingPickup && isDefaultCust;

  return (
    <div
      ref={containerRef}
      className="csc-card"
      style={{
        border: isWarningHighlight ? '1px solid #f59e0b' : '1px solid var(--border)',
        backgroundColor: isWarningHighlight ? 'rgba(245, 158, 11, 0.06)' : 'var(--bg-surface)'
      }}
    >
      {/* Ficha Resumida Superior */}
      <div className="flex-align-center justify-between gap-2">
        <div className="flex-align-center gap-2 csc-fill">
          <div
            className="flex-center flex-shrink-0 csc-avatar"
            style={{
              backgroundColor: isDefaultCust ? 'var(--bg-hover, #374151)' : 'rgba(99, 102, 241, 0.15)',
              color: isDefaultCust ? 'var(--text-muted)' : 'var(--accent-primary, #6366f1)'
            }}
          >
            <User size={18} />
          </div>
          <div className="csc-fill">
            <div className="text-xs text-muted font-medium">Cliente Asignado:</div>
            <div
              className="font-bold text-sm text-primary text-truncate csc-cust-name"
              title={isDefaultCust ? 'Consumidor Final (V-00000000)' : `${currentCustomer.name || currentCustomer.customerName}`}
            >
              {isDefaultCust
                ? 'Consumidor Final (V-00000000)'
                : `${currentCustomer.name || currentCustomer.customerName} ${currentCustomer.cedulaOrRif || currentCustomer.customerCedula ? `(${currentCustomer.cedulaOrRif || currentCustomer.customerCedula})` : ''}`}
            </div>
          </div>
        </div>

        {!readOnly && (
          <button
            type="button"
            disabled={disabled || saving}
            onClick={handleToggleExpand}
            className="btn btn-sm btn-outline-secondary d-inline-flex flex-align-center gap-1 flex-shrink-0 csc-toggle-btn"
            style={{
              borderColor: isWarningHighlight ? '#f59e0b' : 'var(--border)',
              color: isWarningHighlight ? '#f59e0b' : 'var(--text-primary)'
            }}
          >
            {saving ? (
              <Loader2 className="animate-spin" size={14} />
            ) : isExpanded ? (
              <>
                <span>Cerrar</span> <ChevronUp size={14} />
              </>
            ) : isDefaultCust ? (
              <>
                <span>+ Asignar Cliente</span> <ChevronDown size={14} />
              </>
            ) : (
              <>
                <span>✏️ Cambiar</span> <ChevronDown size={14} />
              </>
            )}
          </button>
        )}
      </div>

      {/* Alerta si es necesario asignar cliente para Mercancía en Custodia */}
      {isWarningHighlight && !isExpanded && (
        <div className="text-xs mt-2 font-medium text-warning d-flex flex-align-center gap-1">
          ⚠️ Mercancía en custodia requiere asignar un cliente identificado.
        </div>
      )}

      {/* Buscador Colapsable */}
      {isExpanded && (
        <div className="mt-3 pt-3 border-top csc-relative">
          {error && (
            <div className="alert alert-danger mb-2 py-1 px-2 text-xs csc-radius-6">
              {error}
            </div>
          )}

          {!isCreatingCustomer ? (
            <div className="csc-relative">
              <div className="d-flex gap-2 mb-2 csc-relative">
                <div className="csc-relative flex-1">
                  <input
                    ref={searchInputRef}
                    type="text"
                    className="form-input text-sm csc-search-input"
                    placeholder="Buscar por Nombre o Cédula/RIF..."
                    value={query}
                    onChange={handleSearchChange}
                    onFocus={handleInputFocus}
                    onBlur={handleInputBlur}
                  />
                  <Search size={15} className="csc-search-icon" />
                  {query && (
                    <button
                      type="button"
                      onClick={() => { setQuery(''); setIsDropdownOpen(true); loadCustomers(''); }}
                      className="csc-clear-btn"
                    >
                      <X size={14} />
                    </button>
                  )}
                </div>

                <button
                  type="button"
                  onClick={() => setIsCreatingCustomer(true)}
                  className="btn btn-sm btn-primary d-inline-flex flex-align-center gap-1 flex-shrink-0 text-nowrap font-semibold csc-create-btn"
                >
                  <UserPlus size={14} /> + Crear
                </button>
              </div>

              {/* Lista de Resultados Desplegable Flotante (Floating Dropdown Overlay) */}
              {isDropdownOpen && (
                <div
                  className="custom-scrollbar csc-dropdown"
                  onMouseDown={(e) => {
                    // Evitar que el mousedown robe el foco al input antes del onClick
                    e.preventDefault();
                  }}
                >
                  {loadingCustomers ? (
                    <div className="text-center py-3 text-xs text-muted d-flex flex-align-center justify-center gap-1">
                      <Loader2 className="animate-spin" size={14} /> Buscando clientes...
                    </div>
                  ) : customers.length === 0 ? (
                    <div className="text-center py-3 text-xs text-muted">
                      No se encontraron clientes coincidentes.
                    </div>
                  ) : (
                    (query.trim() ? customers : customers.slice(0, 8)).map((c) => {
                      const isChosen = currentCustomer?.id === c.id;
                      return (
                        <div
                          key={c.id}
                          onMouseDown={(e) => {
                            e.preventDefault();
                          }}
                          onClick={() => handleChooseCustomer(c)}
                          className="d-flex justify-between flex-align-center px-3 py-2 text-left cursor-pointer border-bottom csc-result-item"
                          style={{
                            backgroundColor: isChosen ? 'rgba(99, 102, 241, 0.15)' : 'transparent'
                          }}
                          onMouseEnter={(e) => { if (!isChosen) e.currentTarget.style.backgroundColor = 'var(--bg-hover, rgba(255,255,255,0.06))'; }}
                          onMouseLeave={(e) => { if (!isChosen) e.currentTarget.style.backgroundColor = 'transparent'; }}
                        >
                          <div className="csc-fill">
                            <div
                              className="font-bold text-sm text-primary text-truncate csc-result-name"
                              title={c.name}
                            >
                              {c.name}
                            </div>
                            <div className="text-xs text-muted csc-result-meta">
                              {c.cedulaOrRif} {c.phone ? `• ${c.phone}` : ''}
                            </div>
                          </div>
                          {isChosen && <Check size={16} className="text-primary flex-shrink-0 ml-2" />}
                        </div>
                      );
                    })
                  )}
                </div>
              )}
            </div>
          ) : (
            /* Formulario de Creación de Cliente */
            <form onSubmit={handleCreateCustomerSubmit} className="p-3 border rounded csc-create-form">
              <div className="font-bold text-xs text-primary mb-2">➕ Registrar Nuevo Cliente</div>

              <div className="d-flex gap-2 mb-2">
                <input
                  type="text"
                  className="form-input text-xs csc-create-input"
                  placeholder="Cédula / RIF (ej. V-12345678)"
                  value={newCustomer.cedulaOrRif}
                  onChange={(e) => setNewCustomer({ ...newCustomer, cedulaOrRif: e.target.value })}
                  required
                />
                <input
                  type="text"
                  className="form-input text-xs csc-create-input"
                  placeholder="Nombre completo"
                  value={newCustomer.name}
                  onChange={(e) => setNewCustomer({ ...newCustomer, name: e.target.value })}
                  required
                />
              </div>

              <div className="d-flex gap-2 mb-3">
                <input
                  type="text"
                  className="form-input text-xs csc-create-input"
                  placeholder="Teléfono (Opcional)"
                  value={newCustomer.phone}
                  onChange={(e) => setNewCustomer({ ...newCustomer, phone: e.target.value })}
                />
              </div>

              <div className="d-flex justify-end gap-2">
                <button
                  type="button"
                  className="btn btn-xs btn-outline-secondary csc-cancel-btn"
                  onClick={() => setIsCreatingCustomer(false)}
                >
                  Cancelar
                </button>
                <button
                  type="submit"
                  disabled={saving}
                  className="btn btn-xs btn-primary d-inline-flex flex-align-center gap-1 csc-save-btn"
                >
                  {saving ? <Loader2 className="animate-spin" size={12} /> : <Check size={12} />}
                  Guardar y Asignar
                </button>
              </div>
            </form>
          )}
        </div>
      )}
    </div>
  );
}
