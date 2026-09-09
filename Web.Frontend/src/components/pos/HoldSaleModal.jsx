import React, { useState, useEffect, useRef } from 'react';
import Modal from '../ui/Modal';
import { getCustomers, createCustomer } from '../../services/customerApi';
import { getActivePaymentMethods } from '../../services/paymentApi';
import { holdSale } from '../../services/salesApi';
import { formatNumberEs, formatBsS, formatUSD } from '../../utils/formatters';
import { Search, UserPlus, Clock, Loader2, RefreshCw, X } from 'lucide-react';
import './HoldSaleModal.css';

// Token reutilizable que no es utilitario de escala (borde/borde-redondeado lo da .border)
const cardStyle = {
  borderRadius: '10px',
  padding: '12px',
  backgroundColor: 'var(--bg-surface)',
};

export default function HoldSaleModal({ isOpen, onClose, saleId, currentCustomer, saleTotalUSD, saleTotalBsS = 0, exchangeRate, onSuccess }) {
  const [query, setQuery] = useState('');
  const [customers, setCustomers] = useState([]);
  const [loadingCustomers, setLoadingCustomers] = useState(false);
  const [selectedCustomer, setSelectedCustomer] = useState(null);
  const searchInputRef = useRef(null);
  const searchWrapRef = useRef(null);
  const [isDropdownOpen, setIsDropdownOpen] = useState(false);

  // New customer creation state
  const [isCreatingCustomer, setIsCreatingCustomer] = useState(false);
  const [newCustomer, setNewCustomer] = useState({ cedulaOrRif: '', name: '', phone: '' });

  // Initial Payment state
  const [enablePayment, setEnablePayment] = useState(false);
  const [paymentMethods, setPaymentMethods] = useState([]);
  const [paymentMethodId, setPaymentMethodId] = useState('');
  const [cents, setCents] = useState(0);
  const [isFreshFocus, setIsFreshFocus] = useState(true);
  const [reference, setReference] = useState('');

  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState(null);

  useEffect(() => {
    if (isOpen) {
      loadCustomers('');
      // 8.5-WEB3: reintento ante fallo transitorio de payment-methods.
      const loadMethods = (attempt) => {
        getActivePaymentMethods()
          .then(res => {
            setPaymentMethods(res || []);
            if (res && res.length > 0) setPaymentMethodId(res[0].id.toString());
          })
          .catch(err => {
            console.error('[HoldSaleModal] Error al cargar métodos de pago:', err);
            if (attempt < 1) {
              setTimeout(() => loadMethods(attempt + 1), 800);
            }
          });
      };
      loadMethods(0);

      if (currentCustomer && !currentCustomer.isDefault && currentCustomer.cedulaOrRif !== 'V-00000000') {
        setSelectedCustomer(currentCustomer);
      } else {
        setSelectedCustomer(null);
      }

      setError(null);
      setCents(0);
      setEnablePayment(false);
      setReference('');
      setIsCreatingCustomer(false);
      setIsDropdownOpen(false);
      setQuery('');
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isOpen, currentCustomer?.id, currentCustomer?.cedulaOrRif, currentCustomer?.isDefault]);

  // Close the floating dropdown when clicking outside the search area
  useEffect(() => {
    if (!isOpen || selectedCustomer) return;
    function handleMousedown(e) {
      if (searchWrapRef.current && !searchWrapRef.current.contains(e.target)) {
        setIsDropdownOpen(false);
      }
    }
    document.addEventListener('mousedown', handleMousedown);
    return () => document.removeEventListener('mousedown', handleMousedown);
  }, [isOpen, selectedCustomer]);

  const loadCustomers = async (q) => {
    setLoadingCustomers(true);
    try {
      const data = await getCustomers(q);
      // Ocultar al Consumidor Final (V-00000000 / IsDefault)
      setCustomers((data || []).filter(c => !c.isDefault && c.cedulaOrRif !== 'V-00000000'));
    } catch (err) {
      console.error(err);
    } finally {
      setLoadingCustomers(false);
    }
  };

  const handleSearchChange = (e) => {
    const val = e.target.value;
    setQuery(val);
    setIsDropdownOpen(true);
    loadCustomers(val);
  };

  const handleClearOrChangeCustomer = () => {
    setSelectedCustomer(null);
    setQuery('');
    loadCustomers('');
    setTimeout(() => {
      if (searchInputRef.current) { searchInputRef.current.focus(); setIsDropdownOpen(true); }
    }, 50);
  };

  const handleCreateCustomer = async (e) => {
    e.preventDefault();
    setError(null);
    try {
      const created = await createCustomer({
        cedulaOrRif: newCustomer.cedulaOrRif,
        name: newCustomer.name,
        phone: newCustomer.phone,
      });
      setSelectedCustomer(created);
      setIsCreatingCustomer(false);
      setIsDropdownOpen(false);
      loadCustomers(created.cedulaOrRif);
    } catch (err) {
      setError(err.response?.data || err.message || 'Error al crear cliente');
    }
  };

  // ATM Input Handlers for Initial Payment
  const initialBsS = cents / 100;
  const initialUsd = exchangeRate > 0 ? initialBsS / exchangeRate : 0;
  const remainingUsd = Math.max(0, saleTotalUSD - (enablePayment ? initialUsd : 0));
  const remainingBsS = Math.max(0, (saleTotalBsS > 0 ? saleTotalBsS : remainingUsd * exchangeRate) - (enablePayment ? initialBsS : 0));

  const handleKeyDown = (e) => {
    if (['Tab', 'ArrowLeft', 'ArrowRight', 'Home', 'End', 'Enter'].includes(e.key)) return;
    if (['e', 'E', '+', '-', '.', ','].includes(e.key)) { e.preventDefault(); return; }
    if (e.key === 'Backspace') {
      e.preventDefault();
      if (isFreshFocus) { setCents(0); setIsFreshFocus(false); }
      else setCents(prev => Math.floor(prev / 10));
      return;
    }
    if (e.key === 'Delete') { e.preventDefault(); setCents(0); setIsFreshFocus(false); return; }
    if (/^[0-9]$/.test(e.key)) {
      e.preventDefault();
      const digit = parseInt(e.key, 10);
      if (isFreshFocus) { setCents(digit); setIsFreshFocus(false); }
      else setCents(prev => (prev * 10 + digit > 999999999 ? prev : prev * 10 + digit));
    }
  };

  const handlePaste = (e) => {
    e.preventDefault();
    const raw = e.clipboardData.getData('text').replace(/\D/g, '');
    if (raw) { setCents(parseInt(raw, 10)); setIsFreshFocus(false); }
  };

  const selectedMethod = paymentMethods.find(m => m.id.toString() === paymentMethodId);
  const isCashSelected = !!selectedMethod?.isCash;
  // El efectivo solo acepta montos enteros (sin centavos): se normaliza antes de enviar.
  const finalPaymentBsS = isCashSelected ? Math.trunc(initialBsS) : initialBsS;
  const finalPaymentUsd = exchangeRate > 0 ? finalPaymentBsS / exchangeRate : 0;

  const handleConfirmHold = async () => {
    if (!selectedCustomer) {
      setError('Debes seleccionar o crear un cliente registrado para poner en espera.');
      return;
    }

    if (selectedCustomer.isDefault || selectedCustomer.cedulaOrRif === 'V-00000000') {
      setError('No se permite guardar pedidos en espera a nombre del Consumidor Final.');
      return;
    }

    if (enablePayment && initialBsS > 0 && selectedMethod?.requiresReference && !reference.trim()) {
      setError(`El método de pago (${selectedMethod.name}) requiere número de referencia.`);
      return;
    }

    setSubmitting(true);
    setError(null);

    try {
      const initialPaymentsList = (enablePayment && finalPaymentBsS > 0) ? [{
        paymentMethodId: parseInt(paymentMethodId, 10),
        amountBsS: finalPaymentBsS,
        amountUSD: parseFloat(finalPaymentUsd.toFixed(2)),
        exchangeRate: exchangeRate,
        referenceNumber: reference.trim() || null
      }] : null;

      const request = {
        customerId: selectedCustomer.id,
        exchangeRate: exchangeRate,
        initialPayments: initialPaymentsList
      };

      await holdSale(saleId, request);
      onSuccess();
    } catch (err) {
      setError(err.response?.data || err.message || 'Error al guardar pedido en espera.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Guardar Pedido en Espera" maxWidth="580px">
      <div className="hold-modal-pad">
        {error && <div className="alert alert-danger mb-3 text-center">{error}</div>}

        {/* Customer Selection */}
        <div className="mb-4">
          {selectedCustomer ? (
            <div className="border hold-card">
              <div className="d-flex flex-between flex-align-center mb-2">
                <span className="text-muted font-semibold hold-label-upper">
                  Cliente Asignado
                </span>
                <button
                  type="button"
                  onClick={handleClearOrChangeCustomer}
                  className="d-inline-flex flex-align-center hold-change-btn"
                >
                  <RefreshCw size={13} /> Cambiar Cliente
                </button>
              </div>

              <div className="font-bold text-primary hold-customer-name">
                {selectedCustomer.name}
              </div>
              <div className="text-muted hold-customer-meta">
                {selectedCustomer.cedulaOrRif} {selectedCustomer.phone ? `• ${selectedCustomer.phone}` : ''}
              </div>
            </div>
          ) : (
            <div>
              <label className="form-label font-bold mb-2 hold-form-label">
                Cliente (Requerido)
              </label>
              <div className="d-flex flex-row flex-align-center gap-2 mb-2 w-full">
                <div ref={searchWrapRef} className="hold-search-wrap">
                  <input
                    ref={searchInputRef}
                    type="text"
                    className="form-input text-center hold-search-input"
                    placeholder="Buscar por Nombre o Cédula/RIF..."
                    value={query}
                    onChange={handleSearchChange}
                    onFocus={() => setIsDropdownOpen(true)}
                  />
                  <Search size={16} className="hold-search-icon" />
                  {query && (
                    <button
                      type="button"
                      onClick={() => { setQuery(''); setIsDropdownOpen(true); loadCustomers(''); }}
                      className="hold-search-clear"
                    >
                      <X size={14} />
                    </button>
                  )}

                  {/* Floating results dropdown — overlays content instead of pushing it down */}
                  {isDropdownOpen && !isCreatingCustomer && (
                    <div
                      className="custom-scrollbar hold-dropdown"
                    >
                      {loadingCustomers ? (
                        <div className="d-flex flex-align-center justify-center text-muted p-3 gap-2">
                          <Loader2 className="animate-spin" size={16} /> Buscando clientes...
                        </div>
                      ) : customers.length === 0 ? (
                        <div className="text-muted p-3 text-center text-sm">
                          No se encontraron clientes registrados disponibles.
                        </div>
                      ) : (
                        (!query.trim() ? customers.slice(0, 3) : customers).map((c, idx) => {
                          const isItemChosen = selectedCustomer?.id === c.id;
                          return (
                            <div
                              key={c.id}
                              onClick={() => { setSelectedCustomer(c); setIsDropdownOpen(false); }}
                              className="d-flex flex-between flex-align-center text-left hold-dropdown-item"
                              style={{
                                backgroundColor: isItemChosen ? 'rgba(99, 102, 241, 0.15)' : 'transparent',
                                borderLeft: isItemChosen ? '4px solid var(--accent-primary, #6366f1)' : '4px solid transparent',
                                borderBottom: idx < customers.length - 1 ? '1px solid var(--border)' : 'none'
                              }}
                              onMouseEnter={(e) => {
                                if (!isItemChosen) e.currentTarget.style.backgroundColor = 'var(--bg-hover, rgba(255, 255, 255, 0.05))';
                              }}
                              onMouseLeave={(e) => {
                                if (!isItemChosen) e.currentTarget.style.backgroundColor = 'transparent';
                              }}
                            >
                              <div className="hold-item-main">
                                <strong
                                  className="text-primary text-truncate d-block hold-item-name"
                                  title={c.name}
                                >
                                  {c.name}
                                </strong>
                                <div className="text-muted hold-meta">
                                  {c.cedulaOrRif} {c.phone ? `• ${c.phone}` : ''}
                                </div>
                              </div>
                            </div>
                          );
                        })
                      )}
                    </div>
                  )}
                </div>
                <button
                  type="button"
                  onClick={() => { setIsCreatingCustomer(!isCreatingCustomer); setIsDropdownOpen(false); }}
                  className="d-inline-flex hold-create-btn"
                >
                  <UserPlus size={16} /> Crear Cliente
                </button>
              </div>

              {isCreatingCustomer && (
                <form
                  onSubmit={handleCreateCustomer}
                  className="border"
                  style={{ ...cardStyle, marginBottom: '12px', textAlign: 'center' }}
                >
                  <div className="font-bold mb-2 text-center text-primary">Registrar Nuevo Cliente</div>
                  <div className="d-flex gap-2 mb-2">
                    <input type="text" className="form-input text-center hold-field" placeholder="Cédula/RIF" value={newCustomer.cedulaOrRif} onChange={e => setNewCustomer({...newCustomer, cedulaOrRif: e.target.value})} required />
                    <input type="text" className="form-input text-center hold-field" placeholder="Nombre completo" value={newCustomer.name} onChange={e => setNewCustomer({...newCustomer, name: e.target.value})} required />
                  </div>
                  <div className="d-flex gap-2 mb-2">
                    <input type="text" className="form-input text-center hold-field" placeholder="Teléfono (Opcional)" value={newCustomer.phone} onChange={e => setNewCustomer({...newCustomer, phone: e.target.value})} />
                  </div>
                  <button type="submit" className="btn btn-sm btn-primary">Guardar Cliente</button>
                </form>
              )}
            </div>
          )}
        </div>

        {/* Initial Payment — section title left, toggle right */}
        <div className="mb-4 border hold-card">
          <div
            onClick={() => setEnablePayment(!enablePayment)}
            className="d-flex flex-between flex-align-center w-full cursor-pointer hold-toggle-row"
          >
            <span className="font-bold text-primary hold-toggle-title">
              Registrar Abono
            </span>

            {/* Toggle Switch */}
            <div
              className="hold-toggle-track"
              style={{
                backgroundColor: enablePayment ? '#6366f1' : 'rgba(148, 163, 184, 0.3)'
              }}
            >
              <div
                className="hold-toggle-knob"
                style={{
                  transform: enablePayment ? 'translateX(20px)' : 'translateX(0px)'
                }}
              />
            </div>
          </div>

          {enablePayment && (
            <div className="mt-3 pt-3 border-top">
              <div className="form-group mb-2 text-left">
                <label className="form-label text-primary font-semibold">Método de Pago</label>
                <select
                  className="form-select text-center hold-field"
                  value={paymentMethodId}
                  onChange={(e) => setPaymentMethodId(e.target.value)}
                >
                  {paymentMethods.map(m => (
                    <option key={m.id} value={m.id}>{m.name} {m.isCash ? '(Efectivo)' : ''}</option>
                  ))}
                </select>
              </div>

              <div className="form-group mb-2 text-left">
                <label className="form-label text-primary font-semibold">Monto de Abono (Bs.S) - Entrada ATM</label>
                <input
                  type="text"
                  inputMode="numeric"
                  className="form-input font-bold text-center hold-field"
                  value={formatNumberEs(initialBsS)}
                  onKeyDown={handleKeyDown}
                  onPaste={handlePaste}
                  onFocus={(e) => { setIsFreshFocus(true); e.target.select(); }}
                  onChange={() => {}}
                />
                {isCashSelected && (
                  <div className="text-muted text-xs text-center color-primary hold-cash-hint">
                    El pago en efectivo solo acepta montos enteros.
                  </div>
                )}
                <div className="text-muted hold-subtext text-center">
                  ≈ {formatUSD(initialUsd)} (Tasa: {formatNumberEs(exchangeRate)} Bs/$)
                </div>
              </div>

              {selectedMethod?.requiresReference && (
                <div className="form-group mb-2 text-left">
                  <label className="form-label text-primary font-semibold">Número de Referencia *</label>
                  <input
                    type="text"
                    className="form-input text-center hold-field"
                    placeholder="Ingrese el N° de referencia"
                    value={reference}
                    onChange={(e) => setReference(e.target.value)}
                  />
                </div>
              )}
            </div>
          )}
        </div>

        {/* Summary Box — Bs.S destacado sobre USD */}
        <div className="checkout-summary-box mb-4">
          <div className="checkout-summary-row hold-summary-row">
            <span>Total del Pedido:</span>
            <div className="text-right">
              <div className="font-bold text-nowrap color-primary hold-total-lg">
                {formatBsS(saleTotalUSD * exchangeRate)}
              </div>
              <div className="text-xs text-muted font-medium">
                Ref: {formatUSD(saleTotalUSD)}
              </div>
            </div>
          </div>
          {enablePayment && initialBsS > 0 && (
            <div className="checkout-summary-row text-success hold-summary-row">
              <span>Abono Inicial:</span>
              <div className="text-right">
                <div className="font-bold text-nowrap hold-total-md">
                  {formatBsS(initialBsS)}
                </div>
                <div className="text-xs text-muted font-medium">
                  Ref: {formatUSD(initialUsd)}
                </div>
              </div>
            </div>
          )}
          <div className="checkout-summary-row highlight hold-summary-row">
            <span>Deuda Restante Resultante:</span>
            <div className="text-right">
              <div className="font-bold hold-sale-debt text-nowrap hold-total-lg">
                {formatBsS(remainingBsS)}
              </div>
              <div className="text-xs text-muted font-medium">
                Ref: {formatUSD(remainingUsd)}
              </div>
            </div>
          </div>
        </div>

        {/* Footer Actions */}
        <div className="d-flex flex-row flex-align-center justify-center gap-3 w-full mt-4">
          <button
            type="button"
            className="btn btn-outline hold-footer-btn"
            onClick={onClose}
            disabled={submitting}
          >
            CANCELAR
          </button>
          <button
            type="button"
            onClick={handleConfirmHold}
            disabled={!selectedCustomer || submitting || (enablePayment && isCashSelected && cents % 100 !== 0)}
            className="hold-confirm-btn"
            style={{
              backgroundColor: (!selectedCustomer || submitting) ? 'rgba(148, 163, 184, 0.2)' : '#6366f1',
              color: (!selectedCustomer || submitting) ? '#94a3b8' : '#ffffff',
              border: (!selectedCustomer || submitting) ? '1px solid rgba(148, 163, 184, 0.25)' : '1px solid #6366f1',
              boxShadow: (!selectedCustomer || submitting) ? 'none' : '0 2px 10px rgba(99, 102, 241, 0.45)',
              cursor: (!selectedCustomer || submitting) ? 'not-allowed' : 'pointer',
              opacity: (!selectedCustomer || submitting) ? 0.65 : 1
            }}
          >
            {submitting ? <Loader2 className="animate-spin" size={16} /> : <Clock size={16} />}
            <span>Guardar</span>
          </button>
        </div>
      </div>
    </Modal>
  );
}
