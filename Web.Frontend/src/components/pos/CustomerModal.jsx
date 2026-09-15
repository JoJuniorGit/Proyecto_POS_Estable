import React, { useState, useEffect } from 'react';
import Modal from '../ui/Modal';
import { getCustomers, createCustomer } from '../../services/customerApi';
import useDebounce from '../../hooks/useDebounce';
import { Search, UserPlus, AlertCircle, Check, CheckCircle2 } from 'lucide-react';
import './CustomerModal.css';

const VALID_RIF_PREFIXES = ['V', 'E', 'J', 'G', 'P'];
const VALID_PHONE_PREFIXES = [
  '0412', '0414', '0424', '0416', '0426', '0212', '0241', '0242', '0243', '0244', '0245', '0251', '0276', '0261'
];

export default function CustomerModal({
  isOpen,
  onClose,
  onSelectCustomer,
}) {
  const [tab, setTab] = useState('search'); // 'search' | 'create'
  const [query, setQuery] = useState('');
  const [customers, setCustomers] = useState([]);
  const [loading, setLoading] = useState(false);
  const [selectedCustomer, setSelectedCustomer] = useState(null);
  const [error, setError] = useState(null);

  // Form for new customer
  const [cedulaOrRif, setCedulaOrRif] = useState('');
  const [name, setName] = useState('');
  const [phone, setPhone] = useState('');

  // 8.7-M9: búsqueda de clientes con debounce (250 ms) + AbortController. El input solo cambia
  // estado; el efecto dispara un único fetch por consulta. Se refresca cuando el modal abre.
  const debouncedQuery = useDebounce(query, 250);

  useEffect(() => {
    if (!isOpen) return;
    const controller = new AbortController();
    setLoading(true);
    getCustomers(debouncedQuery, controller.signal)
      .then((data) => {
        setCustomers(data || []);
      })
      .catch((err) => {
        if (err?.name !== 'AbortError') {
          setError('Error al cargar la lista de clientes.');
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [isOpen, debouncedQuery]);

  const handleSearchChange = (e) => {
    setQuery(e.target.value);
  };

  // ── Controlled Input 1: Cédula / RIF (V or E + 7-8 digits) ──
  const handleCedulaChange = (e) => {
    let input = e.target.value.toUpperCase();
    if (!input) {
      setCedulaOrRif('');
      return;
    }
    input = input.trim();
    if (/^\d/.test(input)) {
      input = 'V-' + input;
    }
    const firstChar = input.charAt(0);
    if (!VALID_RIF_PREFIXES.includes(firstChar)) {
      setCedulaOrRif('');
      return;
    }
    let digits = input.substring(1).replace(/\D/g, '');
    if (digits.length > 8) {
      digits = digits.substring(0, 8);
    }
    setCedulaOrRif(`${firstChar}-${digits}`);
  };

  const isCedulaValid = /^[VJEGPvjegp]-\d{7,8}$/.test(cedulaOrRif);

  // ── Controlled Input 2: Phone (11 digits + hyphen operator separator) ──
  const handlePhoneChange = (e) => {
    const val = e.target.value;
    let digits = val.replace(/\D/g, '');
    if (digits.length > 11) {
      digits = digits.substring(0, 11);
    }
    if (digits.length >= 4) {
      const prefix4 = digits.substring(0, 4);
      if (!VALID_PHONE_PREFIXES.includes(prefix4)) {
        digits = digits.substring(0, 3);
      }
    }
    let formatted = digits;
    if (digits.length > 4) {
      formatted = `${digits.substring(0, 4)}-${digits.substring(4)}`;
    }
    setPhone(formatted);
  };

  const phoneDigits = phone.replace(/\D/g, '');
  const isPhoneValid = phoneDigits.length === 11 && VALID_PHONE_PREFIXES.includes(phoneDigits.substring(0, 4));

  const handleCreateCustomer = async (e) => {
    e.preventDefault();
    setError(null);

    if (!cedulaOrRif) {
      setError('La Cédula o RIF es obligatoria.');
      return;
    }
    if (!isCedulaValid) {
      setError('La Cédula o RIF debe tener el formato oficial (ej. V-12345678 con 7 u 8 dígitos).');
      return;
    }
    if (!name.trim()) {
      setError('El Nombre o Razón Social es obligatorio.');
      return;
    }
    if (name.trim().length > 50) {
      setError('El Nombre o Razón Social no puede exceder los 50 caracteres.');
      return;
    }
    if (phone && !isPhoneValid) {
      setError('El teléfono debe tener 11 dígitos y una operadora válida (ej. 0412-1234567).');
      return;
    }

    try {
      const created = await createCustomer({
        cedulaOrRif: cedulaOrRif.trim(),
        name: name.trim(),
        phone: phone.trim(),
      });
      setSelectedCustomer(created);
      if (onSelectCustomer) onSelectCustomer(created.id);
    } catch (err) {
      setError(err.message || 'Error al crear cliente');
    }
  };

  const handleConfirm = () => {
    if (!selectedCustomer) {
      setError('Debes seleccionar o registrar un cliente obligatoriamente.');
      return;
    }

    if (onSelectCustomer) onSelectCustomer(selectedCustomer.id);
  };

  const modalTitle = '👥 Cambiar Cliente';

  return (
    <Modal isOpen={isOpen} onClose={onClose} title={modalTitle} maxWidth="650px">
      {error && (
        <div className="alert alert-danger mb-3 d-flex flex-align-center gap-2">
          <AlertCircle size={20} />
          <span>{error}</span>
        </div>
      )}

      {/* Selector de Pestañas Superiores (50/50 en móvil) */}

      <div className="customer-modal-tabs">
        <button
          type="button"
          className={`btn ${tab === 'search' ? 'btn-primary' : 'btn-outline'}`}
          onClick={() => { setError(null); setTab('search'); }}
        >
          <Search size={16} /> Buscar Cliente
        </button>
        <button
          type="button"
          className={`btn ${tab === 'create' ? 'btn-primary' : 'btn-outline'}`}
          onClick={() => { setError(null); setTab('create'); }}
        >
          <UserPlus size={16} /> Nuevo Cliente
        </button>
      </div>

      <div className="checkout-section">
        {tab === 'search' ? (
          <div>
            <div className="form-group cm-search-wrapper">
              <input
                id="customer-search-input"
                name="customerSearch"
                type="text"
                className="form-control cm-search-input"
                placeholder="Buscar por Cédula/RIF o Nombre..."
                value={query}
                onChange={handleSearchChange}
              />
              <Search size={18} className="cm-search-icon" />
            </div>

            <div className="customer-list border custom-scrollbar cm-customer-list">
              {loading ? (
                <p className="text-center cm-list-state">Cargando clientes...</p>
              ) : !Array.isArray(customers) || customers.length === 0 ? (
                <p className="text-center cm-list-state cm-list-empty">No se encontraron clientes registrados.</p>
              ) : (
                customers.map((c) => {
                  const isSelected = selectedCustomer?.id === c.id;
                  return (
                    <div
                      key={c.id}
                      onClick={() => {
                        setSelectedCustomer(c);
                        if (onSelectCustomer) onSelectCustomer(c.id);
                      }}
                      className="customer-modal-item"
                      style={{
                        backgroundColor: isSelected ? 'var(--primary-light)' : 'transparent',
                        borderLeft: isSelected ? '4px solid var(--primary-color)' : '4px solid transparent',
                      }}
                    >
                      <div className="customer-modal-item-info">
                        <div className="customer-modal-item-name">
                          {c.name}
                        </div>
                        <div className="customer-modal-item-sub">
                          <span>Cédula/RIF: <strong>{c.cedulaOrRif}</strong></span>
                          {c.phone && <span>• Tel: {c.phone}</span>}
                        </div>
                      </div>
                      <button
                        type="button"
                        className="btn btn-sm btn-primary cm-select-btn"
                        onClick={(e) => {
                          e.stopPropagation();
                          setSelectedCustomer(c);
                          if (onSelectCustomer) onSelectCustomer(c.id);
                        }}
                      >
                        <Check size={14} /> Seleccionar
                      </button>
                    </div>
                  );
                })
              )}
            </div>
          </div>
        ) : (
          <form onSubmit={handleCreateCustomer} className="customer-modal-create-form d-flex flex-column cm-create-form">
            <div className="customer-modal-form-grid">
              {/* Cédula / RIF Input Controlado */}
              <div className="form-group mb-0 customer-form-group">
                <label htmlFor="customer-cedula-input">Cédula o RIF *</label>
                <div className="w-full cm-input-wrap">
                  <input
                    id="customer-cedula-input"
                    name="cedulaOrRif"
                    type="text"
                    required
                    className="form-control"
                    placeholder="V-12345678"
                    value={cedulaOrRif}
                    onChange={handleCedulaChange}
                    style={{ paddingRight: isCedulaValid ? '34px' : '12px' }}
                  />
                  {isCedulaValid && (
                    <CheckCircle2
                      size={18}
                      className="cm-valid-icon"
                      title="Formato Cédula/RIF válido"
                    />
                  )}
                </div>
                <small className="form-text text-muted">Ej: V-12345678 (V/E + 7 u 8 dígitos)</small>
              </div>

              {/* Teléfono Input Controlado */}
              <div className="form-group mb-0 customer-form-group">
                <label htmlFor="customer-phone-input">Teléfono *</label>
                <div className="w-full cm-input-wrap">
                  <input
                    id="customer-phone-input"
                    name="phone"
                    type="text"
                    className="form-control"
                    placeholder="0412-1234567"
                    value={phone}
                    onChange={handlePhoneChange}
                    style={{ paddingRight: isPhoneValid ? '34px' : '12px' }}
                  />
                  {isPhoneValid && (
                    <CheckCircle2
                      size={18}
                      className="cm-valid-icon"
                      title="Teléfono de 11 dígitos válido"
                    />
                  )}
                </div>
                <small className="form-text text-muted">Ej: 0412-1234567 (11 dígitos)</small>
              </div>
            </div>

            {/* Nombre o Razón Social (Límite 50 caracteres) */}
            <div className="form-group mb-0 customer-form-group">
              <label htmlFor="customer-name-input">Nombre Completo o Razón Social *</label>
              <div className="w-full">
                <input
                  id="customer-name-input"
                  name="name"
                  type="text"
                  required
                  maxLength={50}
                  className="form-control"
                  placeholder="Juan Pérez"
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                />
              </div>
              <div className="d-flex flex-between flex-align-center w-full mt-1 customer-name-counter-wrapper">
                <small className="form-text text-muted cm-form-hint">Máximo 50 caracteres para facturas impresas</small>
                <span
                  className="cm-name-counter"
                  style={{
                    fontWeight: name.length >= 42 ? '700' : '400',
                    color: name.length >= 42 ? 'var(--warning-color, #d97706)' : 'var(--text-muted, #64748b)',
                  }}
                >
                  {name.length} / 50
                </span>
              </div>
            </div>


            {/* Botón Principal Único de la Pestaña Crear */}
            <div className="customer-modal-footer mt-2">
              <button type="button" className="btn btn-outline flex-1" onClick={onClose}>
                Cancelar
              </button>
              <button type="submit" className="btn btn-primary flex-2">
                <UserPlus size={18} /> Guardar Cliente y Seleccionar
              </button>
            </div>
          </form>

        )}
      </div>

      {/* Footer Buttons - Exclusivo para la pestaña de Búsqueda */}
      {tab === 'search' && (
        <div className="customer-modal-footer">
          <button type="button" className="btn btn-outline flex-1" onClick={onClose}>
            Cancelar
          </button>
          <button 
            type="button" 
            className="btn btn-primary flex-2" 
            onClick={handleConfirm} 
            disabled={!selectedCustomer}
          >
            <Check size={18} /> Confirmar Cliente
          </button>
        </div>
      )}
    </Modal>
  );
}
