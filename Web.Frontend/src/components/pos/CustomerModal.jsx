import React, { useState, useEffect } from 'react';
import Modal from '../ui/Modal';
import { getCustomers, createCustomer } from '../../services/customerApi';
import { Search, UserPlus, ShieldCheck, AlertCircle, Check, CheckCircle2 } from 'lucide-react';

const VALID_RIF_PREFIXES = ['V', 'E', 'J', 'G', 'P'];
const VALID_PHONE_PREFIXES = [
  '0412', '0414', '0424', '0416', '0426', '0212', '0241', '0242', '0243', '0244', '0245', '0251', '0276', '0261'
];

export default function CustomerModal({ isOpen, onClose, onConfirmHold, onSelectCustomer, mode = 'hold', saleTotalUSD, exchangeRate, paymentMethods = [] }) {
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
  const [creditLimitText, setCreditLimitText] = useState('0.00');

  // Initial Payment fields
  const [enableInitialPayment, setEnableInitialPayment] = useState(false);
  const [initialPaymentBsS, setInitialPaymentBsS] = useState('');
  const [paymentMethodId, setPaymentMethodId] = useState('');
  const [referenceNumber, setReferenceNumber] = useState('');

  useEffect(() => {
    if (paymentMethods && paymentMethods.length > 0 && !paymentMethodId) {
      setPaymentMethodId(paymentMethods[0].id);
    }
  }, [paymentMethods, paymentMethodId]);

  useEffect(() => {
    if (isOpen) {
      loadCustomers('');
      setError(null);
    }
  }, [isOpen]);

  const loadCustomers = async (searchQuery) => {
    setLoading(true);
    try {
      const data = await getCustomers(searchQuery);
      if (mode === 'hold') {
        setCustomers((data || []).filter(c => !c.isDefault && c.cedulaOrRif !== 'V-00000000'));
      } else {
        setCustomers(data || []);
      }
    } catch (err) {
      console.error(err);
    } finally {
      setLoading(false);
    }
  };

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

  // ── Controlled Input 3: Credit Limit (ATM Shift Effect 0.0x) ──
  const handleCreditLimitChange = (e) => {
    const val = e.target.value;
    if (!val || val === '0' || val === '0.00') {
      setCreditLimitText('0.00');
      return;
    }
    const digits = val.replace(/\D/g, '').replace(/^0+/, '');
    if (!digits) {
      setCreditLimitText('0.00');
      return;
    }
    const cents = parseInt(digits, 10);
    const dollars = (cents / 100).toFixed(2);
    setCreditLimitText(dollars);
  };

  const numericCreditLimitUSD = parseFloat(creditLimitText) || 0;

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
        creditLimitUSD: numericCreditLimitUSD,
      });
      setSelectedCustomer(created);
      setTab('search');
      setQuery(created.cedulaOrRif);
      loadCustomers(created.cedulaOrRif);
    } catch (err) {
      setError(err.response?.data || err.message || 'Error al crear cliente');
    }
  };

  // Calculations for live preview
  const initialBs = parseFloat(initialPaymentBsS) || 0;
  const initialUsd = exchangeRate > 0 ? initialBs / exchangeRate : 0;
  const remainingDebtUsd = Math.max(0, saleTotalUSD - (enableInitialPayment ? initialUsd : 0));
  const creditLimitUsd = selectedCustomer?.creditLimitUSD || (tab === 'create' ? numericCreditLimitUSD : 0);

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
    if (enableInitialPayment && initialBs > 0) {
      initialPaymentObj = {
        paymentMethodId: parseInt(paymentMethodId),
        amountBsS: initialBs,
        amountUSD: initialUsd,
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
        <div className="alert alert-danger mb-3" style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
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

            <div className="customer-list" style={{ maxHeight: '220px', overflowY: 'auto', border: '1px solid var(--border-color)', borderRadius: '8px', marginTop: '10px' }}>
              {loading ? (
                <p style={{ padding: '15px', textAlign: 'center' }}>Cargando clientes...</p>
              ) : !Array.isArray(customers) || customers.length === 0 ? (
                <p style={{ padding: '15px', textAlign: 'center', color: '#888' }}>No se encontraron clientes registrados.</p>
              ) : (
                customers.map((c) => {
                  const isSelected = selectedCustomer?.id === c.id;
                  return (
                    <div
                      key={c.id}
                      onClick={() => setSelectedCustomer(c)}
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
                      <div className="customer-modal-item-credit">
                        <span className="badge badge-info" style={{ display: 'inline-flex', alignItems: 'center', gap: '4px' }}>
                          <ShieldCheck size={14} /> Crédito: ${c.creditLimitUSD.toFixed(2)}
                        </span>
                      </div>
                    </div>
                  );
                })
              )}
            </div>
          </div>
        ) : (
          <form onSubmit={handleCreateCustomer} className="customer-modal-create-form" style={{ display: 'flex', flexDirection: 'column', gap: '15px' }}>
            <div className="customer-modal-form-grid">
              {/* Cédula / RIF Input Controlado */}
              <div className="form-group mb-0 customer-form-group">
                <label htmlFor="customer-cedula-input">Cédula o RIF *</label>
                <div style={{ position: 'relative', width: '100%' }}>
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
                <div style={{ position: 'relative', width: '100%' }}>
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
              <div style={{ width: '100%' }}>
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
              <div className="d-flex justify-content-between align-items-center w-100 mt-1 customer-name-counter-wrapper">
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


            {/* Límite de Crédito (Efecto Cajero Automático ATM 0.0x) */}
            <div className="form-group mb-0 customer-form-group">
              <label htmlFor="customer-credit-limit-input">Límite de Crédito ($ USD)</label>
              <div style={{ width: '100%' }}>
                <input
                  id="customer-credit-limit-input"
                  name="creditLimitUSD"
                  type="text"
                  className="form-control font-bold"
                  value={creditLimitText}
                  onChange={handleCreditLimitChange}
                  style={{ fontWeight: 'bold' }}
                />
              </div>
              <small className="form-text text-muted">Monto máximo que puede quedar pendiente de pago (formateo ATM)</small>
            </div>

            {/* Botón Principal Único de la Pestaña Crear */}
            <div className="customer-modal-footer mt-2">
              <button type="button" className="btn btn-outline" onClick={onClose} style={{ flex: 1 }}>
                Cancelar
              </button>
              <button type="submit" className="btn btn-primary" style={{ flex: 2, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: '8px' }}>
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
            <label className="checkbox-container" htmlFor="enable-initial-payment" style={{ display: 'flex', alignItems: 'center', gap: '8px', cursor: 'pointer', fontWeight: 'bold' }}>
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
              <div className="row mt-3 p-3 bg-light rounded border">
                <div className="col-md-6 form-group mb-0">
                  <label htmlFor="initial-payment-bss">Monto Abonado (Bs.S)</label>
                  <input
                    id="initial-payment-bss"
                    name="initialPaymentBsS"
                    type="number"
                    step="0.01"
                    className="form-control"
                    placeholder="Monto en Bolívares"
                    value={initialPaymentBsS}
                    onChange={(e) => setInitialPaymentBsS(e.target.value)}
                  />
                  <span className="form-text text-muted">Equivale a: ${initialUsd.toFixed(2)} USD</span>
                </div>

                <div className="col-md-6 form-group mb-0">
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
                <span className="font-bold">${saleTotalUSD.toFixed(2)}</span>
              </div>
              <div className="checkout-summary-row text-success">
                <span>Abono Inicial:</span>
                <span className="font-bold">-${initialUsd.toFixed(2)}</span>
              </div>
              <div className="checkout-summary-row highlight">
                <span>Deuda Restante:</span>
                <span className="font-bold">${remainingDebtUsd.toFixed(2)}</span>
              </div>
              <hr className="my-2" />
              <div className="d-flex justify-content-between align-items-center">
                <span className="text-muted">Límite de Crédito del Cliente:</span>
                <span className="font-bold">${creditLimitUsd.toFixed(2)}</span>
              </div>
            </div>
          )}
        </>
      )}

      {/* Footer Buttons - Exclusivo para la pestaña de Búsqueda */}
      {tab === 'search' && (
        <div className="customer-modal-footer">
          <button type="button" className="btn btn-outline" onClick={onClose} style={{ flex: 1 }}>
            Cancelar
          </button>
          <button 
            type="button" 
            className="btn btn-primary" 
            onClick={handleConfirm} 
            disabled={!selectedCustomer}
            style={{ flex: 2, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: '8px' }}
          >
            <Check size={18} /> {mode === 'hold' ? 'Confirmar y Guardar en Espera' : 'Confirmar Cliente'}
          </button>
        </div>
      )}
    </Modal>
  );
}

