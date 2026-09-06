import React, { useState, useEffect, useCallback } from 'react';
import Modal from '../ui/Modal';
import { getCustomers, createCustomer } from '../../services/customerApi';
import { Search, UserPlus, AlertCircle, Check, CheckCircle2 } from 'lucide-react';
import { formatBsS } from '../../utils/formatters';

const VALID_RIF_PREFIXES = ['V', 'E', 'J', 'G', 'P'];
const VALID_PHONE_PREFIXES = [
  '0412', '0414', '0424', '0416', '0426', '0212', '0241', '0242', '0243', '0244', '0245', '0251', '0276', '0261'
];

export default function CustomerModal({
  isOpen,
  onClose,
  onConfirmHold,
  onSelectCustomer,
  mode = 'select',
  saleTotalUSD = 0,
  exchangeRate = 1,
  paymentMethods = []
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
  // Initial Payment fields
  const [enableInitialPayment, setEnableInitialPayment] = useState(false);
  const [initialPaymentBsS, setInitialPaymentBsS] = useState('');
  const [paymentMethodId, setPaymentMethodId] = useState('');

  useEffect(() => {
    if (paymentMethods && paymentMethods.length > 0 && !paymentMethodId) {
      setPaymentMethodId(paymentMethods[0].id);
    }
  }, [paymentMethods, paymentMethodId]);

  const loadCustomers = useCallback(async (searchQuery) => {
    setLoading(true);
    try {
      const data = await getCustomers(searchQuery);
      if (mode === 'hold') {
        setCustomers((data || []).filter(c => !c.isDefault && c.cedulaOrRif !== 'V-00000000'));
      } else {
        setCustomers(data || []);
      }
    } catch {
      setError('Error al cargar la lista de clientes.');
    } finally {
      setLoading(false);
    }
  }, [mode]);

  useEffect(() => {
    if (isOpen) {
      loadCustomers('');
      setError(null);
    }
  }, [isOpen, loadCustomers]);

  const handleSearchChange = (e) => {
    const val = e.target.value;
    setQuery(val);
    loadCustomers(val);
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
      if (mode === 'select') {
        if (onSelectCustomer) onSelectCustomer(created.id);
        return;
      }
      setTab('search');
      setQuery(created.cedulaOrRif);
      loadCustomers(created.cedulaOrRif);
    } catch (err) {
      setError(err.response?.data || err.message || 'Error al crear cliente');
    }
  };

  // Calculations for live preview (defensive against undefined/null values)
  const safeSaleTotalUSD = typeof saleTotalUSD === 'number' && !isNaN(saleTotalUSD) ? saleTotalUSD : 0;
  const safeExchangeRate = typeof exchangeRate === 'number' && exchangeRate > 0 ? exchangeRate : 1;
  const initialBs = parseFloat(initialPaymentBsS) || 0;
  const initialUsd = safeExchangeRate > 0 ? initialBs / safeExchangeRate : 0;
  const remainingDebtUsd = Math.max(0, safeSaleTotalUSD - (enableInitialPayment ? initialUsd : 0));

  // El efectivo solo acepta montos enteros (sin centavos)
  const selectedMethod = paymentMethods.find((m) => String(m.id) === String(paymentMethodId));
  const isCashSelected = !!selectedMethod?.isCash;
  const finalInitialBs = isCashSelected ? Math.trunc(initialBs) : initialBs;
  const finalInitialUsd = safeExchangeRate > 0 ? finalInitialBs / safeExchangeRate : 0;

  const handleConfirm = () => {
    if (!selectedCustomer) {
      setError('Debes seleccionar o registrar un cliente obligatoriamente.');
      return;
    }
    
    if (mode === 'hold' && selectedCustomer.cedulaOrRif === 'V-00000000') {
      setError('Las ventas en espera requieren un cliente real identificable. Registre o seleccione un cliente distinto al Consumidor Final.');
      return;
    }
    if (mode === 'select') {
      if (onSelectCustomer) onSelectCustomer(selectedCustomer.id);
      return;
    }

    let initialPaymentObj = null;
    if (enableInitialPayment && finalInitialBs > 0) {
      initialPaymentObj = {
        paymentMethodId: parseInt(paymentMethodId),
        amountBsS: finalInitialBs,
        amountUSD: finalInitialUsd,
        exchangeRate: exchangeRate,
        referenceNumber: referenceNumber,
      };
    }

    onConfirmHold(selectedCustomer.id, initialPaymentObj);
  };

  const modalTitle = mode === 'hold' ? "🔒 Asignar Cliente - Pedido en Espera" : "👥 Cambiar Cliente";

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
            <div className="form-group" style={{ position: 'relative' }}>
              <input
                id="customer-search-input"
                name="customerSearch"
                type="text"
                className="form-control"
                placeholder="Buscar por Cédula/RIF o Nombre..."
                value={query}
                onChange={handleSearchChange}
                style={{ paddingLeft: '35px' }}
              />
              <Search size={18} style={{ position: 'absolute', left: '10px', top: '10px', opacity: 0.5 }} />
            </div>

            <div className="customer-list border custom-scrollbar" style={{ maxHeight: '220px', overflowY: 'auto', borderRadius: '8px', marginTop: '10px' }}>
              {loading ? (
                <p className="text-center" style={{ padding: '15px' }}>Cargando clientes...</p>
              ) : !Array.isArray(customers) || customers.length === 0 ? (
                <p className="text-center" style={{ padding: '15px', color: '#888' }}>No se encontraron clientes registrados.</p>
              ) : (
                customers.map((c) => {
                  const isSelected = selectedCustomer?.id === c.id;
                  return (
                    <div
                      key={c.id}
                      onClick={() => {
                        setSelectedCustomer(c);
                        if (mode === 'select') {
                          if (onSelectCustomer) onSelectCustomer(c.id);
                        }
                      }}
                      className="customer-modal-item"
                      style={{
                        backgroundColor: isSelected ? 'var(--primary-light)' : 'transparent',
                        borderLeft: isSelected ? '4px solid var(--primary-color)' : '4px solid transparent',
                        cursor: 'pointer',
                        display: 'flex',
                        alignItems: 'center',
                        justifyContent: 'space-between',
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
                      {mode === 'select' && (
                        <button
                          type="button"
                          className="btn btn-sm btn-primary"
                          style={{ marginLeft: '12px', padding: '5px 12px', fontSize: '0.8rem', whiteSpace: 'nowrap' }}
                          onClick={(e) => {
                            e.stopPropagation();
                            setSelectedCustomer(c);
                            if (onSelectCustomer) onSelectCustomer(c.id);
                          }}
                        >
                          <Check size={14} /> Seleccionar
                        </button>
                      )}
                    </div>
                  );
                })
              )}
            </div>
          </div>
        ) : (
          <form onSubmit={handleCreateCustomer} className="customer-modal-create-form d-flex flex-column" style={{ gap: '15px' }}>
            <div className="customer-modal-form-grid">
              {/* Cédula / RIF Input Controlado */}
              <div className="form-group mb-0 customer-form-group">
                <label htmlFor="customer-cedula-input">Cédula o RIF *</label>
                <div className="w-full" style={{ position: 'relative' }}>
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
                      style={{ position: 'absolute', right: '10px', top: '10px', color: 'var(--success-color, #10b981)' }}
                      title="Formato Cédula/RIF válido"
                    />
                  )}
                </div>
                <small className="form-text text-muted">Ej: V-12345678 (V/E + 7 u 8 dígitos)</small>
              </div>

              {/* Teléfono Input Controlado */}
              <div className="form-group mb-0 customer-form-group">
                <label htmlFor="customer-phone-input">Teléfono *</label>
                <div className="w-full" style={{ position: 'relative' }}>
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
                      style={{ position: 'absolute', right: '10px', top: '10px', color: 'var(--success-color, #10b981)' }}
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
                <small className="form-text text-muted" style={{ margin: 0 }}>Máximo 50 caracteres para facturas impresas</small>
                <span
                  style={{
                    fontSize: '0.8rem',
                    fontWeight: name.length >= 42 ? '700' : '400',
                    color: name.length >= 42 ? 'var(--warning-color, #d97706)' : 'var(--text-muted, #64748b)',
                    whiteSpace: 'nowrap'
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

      {mode === 'hold' && tab === 'search' && (
        <>
          {/* Abono Inicial Sección */}
          <div className="checkout-section">
            <label className="d-flex flex-align-center gap-2 cursor-pointer font-bold" htmlFor="enable-initial-payment">
              <input
                id="enable-initial-payment"
                name="enableInitialPayment"
                type="checkbox"
                checked={enableInitialPayment}
                onChange={(e) => setEnableInitialPayment(e.target.checked)}
              />
              Registrar Abono Inicial en esta transacción
            </label>

            {enableInitialPayment && (
              <div className="d-flex flex-wrap gap-2 mt-3 p-3 border">
                <div className="flex-1 form-group mb-0" style={{ minWidth: '200px' }}>
                  <label htmlFor="initial-payment-bss">Monto Abonado (Bs.S)</label>
                  <input
                    id="initial-payment-bss"
                    name="initialPaymentBsS"
                    type="number"
                    step={isCashSelected ? 1 : 0.01}
                    min="1"
                    className="form-control"
                    placeholder="Monto en Bolívares"
                    value={initialPaymentBsS}
                    onChange={(e) => setInitialPaymentBsS(e.target.value)}
                    onKeyDown={(e) => {
                      if (isCashSelected && ['.', ',', 'e', 'E', '+', '-'].includes(e.key)) {
                        e.preventDefault();
                      }
                    }}
                  />
                  {isCashSelected ? (
                    <span className="form-text" style={{ color: 'var(--accent-primary, #6366f1)' }}>El pago en efectivo solo acepta montos enteros.</span>
                  ) : (
                    <span className="form-text text-muted">Equivale a: ${initialUsd.toFixed(2)} USD</span>
                  )}
                </div>

                <div className="flex-1 form-group mb-0" style={{ minWidth: '200px' }}>
                  <label htmlFor="payment-method-id">Método de Pago</label>
                  <select
                    id="payment-method-id"
                    name="paymentMethodId"
                    className="form-control"
                    value={paymentMethodId}
                    onChange={(e) => setPaymentMethodId(e.target.value)}
                  >
                    {paymentMethods.map((m) => (
                      <option key={m.id} value={m.id}>{m.name}</option>
                    ))}
                  </select>
                </div>
              </div>
            )}
          </div>

          {/* Resumen Financiero */}
          {selectedCustomer && (
            <div className="checkout-summary-box" style={{ marginTop: '15px' }}>
              <div className="checkout-summary-row">
                <span>Total Pedido:</span>
                <div style={{ textAlign: 'right' }}>
                  <div className="font-bold text-primary" style={{ fontSize: '1.1rem' }}>
                    {formatBsS(safeSaleTotalUSD * safeExchangeRate)}
                  </div>
                  <div className="text-xs text-muted">
                    Ref: ${safeSaleTotalUSD.toFixed(2)} USD
                  </div>
                </div>
              </div>
              <div className="checkout-summary-row text-success">
                <span>Abono Inicial:</span>
                <div style={{ textAlign: 'right' }}>
                  <div className="font-bold">
                    {formatBsS(finalInitialBs)}
                  </div>
                  <div className="text-xs text-muted">
                    Ref: -${initialUsd.toFixed(2)} USD
                  </div>
                </div>
              </div>
              <div className="checkout-summary-row highlight">
                <span>Deuda Restante:</span>
                <div style={{ textAlign: 'right' }}>
                  <div className="font-bold" style={{ fontSize: '1.1rem' }}>
                    {formatBsS(remainingDebtUsd * safeExchangeRate)}
                  </div>
                  <div className="text-xs text-muted">
                    Ref: ${remainingDebtUsd.toFixed(2)} USD
                  </div>
                </div>
              </div>
            </div>
          )}
        </>
      )}

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
            disabled={!selectedCustomer || (enableInitialPayment && isCashSelected && initialBs % 1 !== 0)}
          >
            <Check size={18} /> {mode === 'hold' ? 'Guardar' : 'Confirmar Cliente'}
          </button>
        </div>
      )}
    </Modal>
  );
}
