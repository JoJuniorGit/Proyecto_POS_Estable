import { useState, useEffect, useRef } from 'react';
import { User, Search, UserPlus, X, ChevronDown, ChevronUp, Loader2, Check } from 'lucide-react';
import { getCustomers, createCustomer } from '../../services/customerApi';

export default function CustomerSelectorCard({
  currentCustomer,
  isPendingPickup = false,
  forceExpand = false,
  onSelectCustomer,
  disabled = false
}) {
  const [isExpanded, setIsExpanded] = useState(false);
  const [query, setQuery] = useState('');
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

  const custName = (currentCustomer?.name || currentCustomer?.customerName || '').toLowerCase();
  const isDefaultCust = !currentCustomer?.id || 
    currentCustomer?.isDefault || 
    currentCustomer?.cedulaOrRif === 'V-00000000' ||
    custName.includes('consumidor final') || 
    custName.includes('general');

  // Auto-expandir cuando se marca Mercancía en Custodia y el cliente es Consumidor Final
  useEffect(() => {
    if (forceExpand || (isPendingPickup && isDefaultCust)) {
      setIsExpanded(true);
      setIsDropdownOpen(true);
      loadCustomers('');
      setTimeout(() => {
        if (searchInputRef.current) searchInputRef.current.focus();
      }, 80);
    }
  }, [forceExpand, isPendingPickup, isDefaultCust]);

  // Manejar tecla Escape para cerrar dropdown
  useEffect(() => {
    function handleKeyDown(e) {
      if (e.key === 'Escape' && isExpanded && !isPendingPickup) {
        setIsExpanded(false);
        setIsDropdownOpen(false);
        setIsCreatingCustomer(false);
      }
    }
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [isExpanded, isPendingPickup]);

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

  const handleToggleExpand = () => {
    if (disabled) return;
    const nextState = !isExpanded;
    setIsExpanded(nextState);
    setError(null);
    if (nextState) {
      setIsDropdownOpen(true);
      loadCustomers('');
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
    loadCustomers(val);
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
      style={{
        borderRadius: '10px',
        border: isWarningHighlight ? '1px solid #f59e0b' : '1px solid var(--border)',
        backgroundColor: isWarningHighlight ? 'rgba(245, 158, 11, 0.06)' : 'var(--bg-surface)',
        padding: '10px 14px',
        marginBottom: '12px',
        transition: 'all 0.2s ease',
        position: 'relative'
      }}
    >
      {/* Ficha Resumida Superior */}
      <div className="flex-align-center justify-between gap-2">
        <div className="flex-align-center gap-2" style={{ minWidth: 0, flex: 1 }}>
          <div
            style={{
              width: '32px',
              height: '32px',
              borderRadius: '50%',
              backgroundColor: isDefaultCust ? 'var(--bg-hover, #374151)' : 'rgba(99, 102, 241, 0.15)',
              color: isDefaultCust ? 'var(--text-muted)' : 'var(--accent-primary, #6366f1)',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              flexShrink: 0
            }}
          >
            <User size={18} />
          </div>
          <div style={{ minWidth: 0, flex: 1 }}>
            <div className="text-xs text-muted font-medium">Cliente Asignado:</div>
            <div className="font-bold text-sm text-truncate text-primary" style={{ lineHeight: '1.2' }}>
              {isDefaultCust
                ? 'Consumidor Final (V-00000000)'
                : `${currentCustomer.name || currentCustomer.customerName} ${currentCustomer.cedulaOrRif || currentCustomer.customerCedula ? `(${currentCustomer.cedulaOrRif || currentCustomer.customerCedula})` : ''}`}
            </div>
          </div>
        </div>

        <button
          type="button"
          disabled={disabled || saving}
          onClick={handleToggleExpand}
          className="btn btn-sm btn-outline-secondary flex-align-center gap-1"
          style={{
            fontSize: '0.8rem',
            padding: '4px 10px',
            borderRadius: '6px',
            borderColor: isWarningHighlight ? '#f59e0b' : undefined,
            color: isWarningHighlight ? '#f59e0b' : undefined,
            flexShrink: 0
          }}
        >
          {saving ? (
            <Loader2 className="animate-spin" size={14} />
          ) : isExpanded ? (
            <>
              Cerrar <ChevronUp size={14} />
            </>
          ) : isDefaultCust ? (
            <>
              + Asignar Cliente <ChevronDown size={14} />
            </>
          ) : (
            <>
              ✏️ Cambiar <ChevronDown size={14} />
            </>
          )}
        </button>
      </div>

      {/* Alerta si es necesario asignar cliente para Mercancía en Custodia */}
      {isWarningHighlight && !isExpanded && (
        <div className="text-xs mt-2 font-medium text-warning d-flex flex-align-center gap-1">
          ⚠️ Mercancía en custodia requiere asignar un cliente identificado.
        </div>
      )}

      {/* Buscador Colapsable */}
      {isExpanded && (
        <div className="mt-3 pt-3 border-top" style={{ borderColor: 'var(--border)' }}>
          {error && (
            <div className="alert alert-danger mb-2 py-1 px-2 text-xs" style={{ borderRadius: '6px' }}>
              {error}
            </div>
          )}

          {!isCreatingCustomer ? (
            <div>
              <div className="d-flex gap-2 mb-2" style={{ position: 'relative' }}>
                <div style={{ position: 'relative', flex: 1 }}>
                  <input
                    ref={searchInputRef}
                    type="text"
                    className="form-input text-sm"
                    placeholder="Buscar por Nombre o Cédula/RIF..."
                    value={query}
                    onChange={handleSearchChange}
                    onFocus={() => setIsDropdownOpen(true)}
                    style={{
                      paddingLeft: '32px',
                      paddingRight: '28px',
                      backgroundColor: 'var(--bg-input)',
                      color: 'var(--text-primary)',
                      borderColor: 'var(--border)'
                    }}
                  />
                  <Search size={15} style={{ position: 'absolute', left: '10px', top: '50%', transform: 'translateY(-50%)', color: 'var(--text-muted)' }} />
                  {query && (
                    <button
                      type="button"
                      onClick={() => { setQuery(''); setIsDropdownOpen(true); loadCustomers(''); }}
                      style={{ position: 'absolute', right: '8px', top: '50%', transform: 'translateY(-50%)', background: 'none', border: 'none', color: 'var(--text-muted)', cursor: 'pointer' }}
                    >
                      <X size={14} />
                    </button>
                  )}
                </div>

                <button
                  type="button"
                  onClick={() => setIsCreatingCustomer(true)}
                  className="btn btn-sm btn-outline-primary d-inline-flex flex-align-center gap-1"
                  style={{ fontSize: '0.8rem', whiteSpace: 'nowrap', flexShrink: 0 }}
                >
                  <UserPlus size={14} /> + Crear
                </button>
              </div>

              {/* Lista de Resultados Desplegable */}
              {isDropdownOpen && (
                <div
                  className="custom-scrollbar mt-1"
                  style={{
                    maxHeight: '180px',
                    overflowY: 'auto',
                    backgroundColor: 'var(--bg-card, var(--bg-surface))',
                    border: '1px solid var(--border)',
                    borderRadius: '8px'
                  }}
                >
                  {loadingCustomers ? (
                    <div className="text-center py-2 text-xs text-muted d-flex flex-align-center justify-center gap-1">
                      <Loader2 className="animate-spin" size={14} /> Buscando clientes...
                    </div>
                  ) : customers.length === 0 ? (
                    <div className="text-center py-2 text-xs text-muted">
                      No se encontraron clientes coincidentes.
                    </div>
                  ) : (
                    customers.map((c) => {
                      const isChosen = currentCustomer?.id === c.id;
                      return (
                        <div
                          key={c.id}
                          onClick={() => handleChooseCustomer(c)}
                          className="d-flex justify-between flex-align-center px-3 py-2 text-left"
                          style={{
                            cursor: 'pointer',
                            backgroundColor: isChosen ? 'rgba(99, 102, 241, 0.15)' : 'transparent',
                            borderBottom: '1px solid var(--border)',
                            transition: 'background-color 0.15s ease'
                          }}
                          onMouseEnter={(e) => { if (!isChosen) e.currentTarget.style.backgroundColor = 'var(--bg-hover, rgba(255,255,255,0.05))'; }}
                          onMouseLeave={(e) => { if (!isChosen) e.currentTarget.style.backgroundColor = 'transparent'; }}
                        >
                          <div style={{ flex: 1, minWidth: 0 }}>
                            <div className="font-bold text-sm text-primary text-truncate">{c.name}</div>
                            <div className="text-xs text-muted">{c.cedulaOrRif} {c.phone ? `• ${c.phone}` : ''}</div>
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
            <form onSubmit={handleCreateCustomerSubmit} className="p-2 border rounded" style={{ backgroundColor: 'var(--bg-input)', borderColor: 'var(--border)' }}>
              <div className="d-flex justify-between flex-align-center mb-2">
                <span className="font-bold text-xs text-primary">➕ Registrar Nuevo Cliente</span>
                <button
                  type="button"
                  onClick={() => setIsCreatingCustomer(false)}
                  style={{ background: 'none', border: 'none', color: 'var(--text-muted)', cursor: 'pointer' }}
                >
                  <X size={14} />
                </button>
              </div>

              <div className="d-flex gap-2 mb-2">
                <input
                  type="text"
                  className="form-input text-xs"
                  placeholder="Cédula / RIF (ej. V-12345678)"
                  value={newCustomer.cedulaOrRif}
                  onChange={(e) => setNewCustomer({ ...newCustomer, cedulaOrRif: e.target.value })}
                  required
                />
                <input
                  type="text"
                  className="form-input text-xs"
                  placeholder="Nombre completo"
                  value={newCustomer.name}
                  onChange={(e) => setNewCustomer({ ...newCustomer, name: e.target.value })}
                  required
                />
              </div>

              <div className="d-flex gap-2 mb-2">
                <input
                  type="text"
                  className="form-input text-xs"
                  placeholder="Teléfono (Opcional)"
                  value={newCustomer.phone}
                  onChange={(e) => setNewCustomer({ ...newCustomer, phone: e.target.value })}
                />
              </div>

              <div className="d-flex justify-end gap-2">
                <button
                  type="button"
                  className="btn btn-xs btn-outline-secondary"
                  onClick={() => setIsCreatingCustomer(false)}
                >
                  Cancelar
                </button>
                <button
                  type="submit"
                  disabled={saving}
                  className="btn btn-xs btn-primary d-inline-flex flex-align-center gap-1"
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
