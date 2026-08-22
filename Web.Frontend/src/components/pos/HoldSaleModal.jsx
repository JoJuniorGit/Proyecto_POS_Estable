import React, { useState, useEffect, useRef } from 'react';
import Modal from '../ui/Modal';
import { getCustomers, createCustomer } from '../../services/customerApi';
import { getActivePaymentMethods } from '../../services/paymentApi';
import { holdSale } from '../../services/salesApi';
import { formatNumberEs, formatBsS, formatUSD } from '../../utils/formatters';
import { Search, UserPlus, Clock, Loader2, RefreshCw, X } from 'lucide-react';

// Tokens reutilizables que no son utilitarios de escala (borde/borde-redondeado lo da .border)
const cardStyle = {
  borderRadius: '10px',
  padding: '12px',
  backgroundColor: 'var(--bg-surface)',
};

const fieldStyle = {
  backgroundColor: 'var(--bg-input)',
  color: 'var(--text-primary)',
  borderColor: 'var(--border)',
};

const summaryRowStyle = {
  display: 'grid',
  gridTemplateColumns: '1fr auto auto',
  gap: '16px',
  alignItems: 'baseline',
  width: '100%',
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
      getActivePaymentMethods()
        .then(res => {
          setPaymentMethods(res || []);
          if (res && res.length > 0) setPaymentMethodId(res[0].id.toString());
        })
        .catch(console.error);

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
  }, [isOpen, currentCustomer]);

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
      <div style={{ padding: '0 6px' }}>
        {error && <div className="alert alert-danger mb-3 text-center">{error}</div>}

        {/* Customer Selection */}
        <div className="mb-4">
          {selectedCustomer ? (
            <div className="border" style={cardStyle}>
              <div className="d-flex flex-between flex-align-center mb-2">
                <span className="text-muted font-semibold" style={{ fontSize: '0.8rem', textTransform: 'uppercase' }}>
                  Cliente Asignado
                </span>
                <button
                  type="button"
                  onClick={handleClearOrChangeCustomer}
                  className="d-inline-flex flex-align-center"
                  style={{
                    background: 'none',
                    border: 'none',
                    padding: 0,
                    cursor: 'pointer',
                    gap: '4px',
                    fontSize: '0.8rem',
                    color: 'var(--accent-primary)',
                    fontWeight: '600'
                  }}
                >
                  <RefreshCw size={13} /> Cambiar Cliente
                </button>
              </div>

              <div className="font-bold text-primary" style={{ fontSize: '1.05rem' }}>
                {selectedCustomer.name}
              </div>
              <div className="text-muted" style={{ fontSize: '0.85rem', marginTop: '2px' }}>
                {selectedCustomer.cedulaOrRif} {selectedCustomer.phone ? `• ${selectedCustomer.phone}` : ''}
              </div>
            </div>
          ) : (
            <div>
              <label className="form-label font-bold mb-2" style={{ fontSize: '0.95rem', color: 'var(--text-primary)' }}>
                Cliente (Requerido)
              </label>
              <div className="d-flex flex-row flex-align-center gap-2 mb-2 w-full">
                <div ref={searchWrapRef} style={{ position: 'relative', flex: '1 1 auto', minWidth: 0 }}>
                  <input
                    ref={searchInputRef}
                    type="text"
                    className="form-input text-center"
                    placeholder="Buscar por Nombre o Cédula/RIF..."
                    value={query}
                    onChange={handleSearchChange}
                    onFocus={() => setIsDropdownOpen(true)}
                    style={{
                      paddingLeft: '36px',
                      paddingRight: '36px',
                      height: '38px',
                      width: '100%',
                      boxSizing: 'border-box',
                      ...fieldStyle
                    }}
                  />
                  <Search size={16} style={{ position: 'absolute', left: '12px', top: '50%', transform: 'translateY(-50%)', color: 'var(--text-muted)' }} />
                  {query && (
                    <button
                      type="button"
                      onClick={() => { setQuery(''); setIsDropdownOpen(true); loadCustomers(''); }}
                      style={{ position: 'absolute', right: '10px', top: '50%', transform: 'translateY(-50%)', background: 'none', border: 'none', color: 'var(--text-muted)', cursor: 'pointer' }}
                    >
                      <X size={14} />
                    </button>
                  )}

                  {/* Floating results dropdown — overlays content instead of pushing it down */}
                  {isDropdownOpen && !isCreatingCustomer && (
                    <div
                      className="custom-scrollbar"
                      style={{
                        position: 'absolute',
                        top: 'calc(100% + 6px)',
                        left: 0,
                        right: 0,
                        zIndex: 40,
                        maxHeight: '240px',
                        overflowY: 'auto',
                        backgroundColor: 'var(--bg-surface)',
                        border: '1px solid var(--border)',
                        borderRadius: '10px',
                        boxShadow: '0 12px 32px rgba(0, 0, 0, 0.35)'
                      }}
                    >
                      {loadingCustomers ? (
                        <div className="d-flex flex-align-center justify-center text-muted" style={{ padding: '12px', gap: '8px' }}>
                          <Loader2 className="animate-spin" size={16} /> Buscando clientes...
                        </div>
                      ) : customers.length === 0 ? (
                        <div className="text-muted" style={{ padding: '12px', textAlign: 'center', fontSize: '0.875rem' }}>
                          No se encontraron clientes registrados disponibles.
                        </div>
                      ) : (
                        (!query.trim() ? customers.slice(0, 3) : customers).map((c, idx) => {
                          const isItemChosen = selectedCustomer?.id === c.id;
                          return (
                            <div
                              key={c.id}
                              onClick={() => { setSelectedCustomer(c); setIsDropdownOpen(false); }}
                              className="d-flex flex-between flex-align-center text-left"
                              style={{
                                cursor: 'pointer',
                                padding: '10px 12px',
                                backgroundColor: isItemChosen ? 'rgba(99, 102, 241, 0.15)' : 'transparent',
                                borderLeft: isItemChosen ? '4px solid var(--accent-primary, #6366f1)' : '4px solid transparent',
                                borderBottom: idx < customers.length - 1 ? '1px solid var(--border)' : 'none',
                                transition: 'background-color 0.15s ease',
                                width: '100%',
                                boxSizing: 'border-box'
                              }}
                              onMouseEnter={(e) => {
                                if (!isItemChosen) e.currentTarget.style.backgroundColor = 'var(--bg-hover, rgba(255, 255, 255, 0.05))';
                              }}
                              onMouseLeave={(e) => {
                                if (!isItemChosen) e.currentTarget.style.backgroundColor = 'transparent';
                              }}
                            >
                              <div style={{ flex: '1 1 auto', minWidth: 0, paddingRight: '12px' }}>
                                <strong
                                  className="text-primary text-truncate d-block"
                                  style={{ fontSize: '0.925rem' }}
                                  title={c.name}
                                >
                                  {c.name}
                                </strong>
                                <div className="text-muted" style={{ fontSize: '0.8rem', marginTop: '2px' }}>
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
                  className="d-inline-flex"
                  style={{
                    height: '38px',
                    flexShrink: 0,
                    whiteSpace: 'nowrap',
                    alignItems: 'center',
                    justifyContent: 'center',
                    gap: '6px',
                    padding: '0 12px',
                    borderRadius: '8px',
                    border: '1px solid var(--border)',
                    backgroundColor: 'transparent',
                    color: 'var(--text-primary)',
                    fontSize: '0.875rem',
                    cursor: 'pointer'
                  }}
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
                    <input type="text" className="form-input text-center" placeholder="Cédula/RIF" value={newCustomer.cedulaOrRif} onChange={e => setNewCustomer({...newCustomer, cedulaOrRif: e.target.value})} required style={fieldStyle} />
                    <input type="text" className="form-input text-center" placeholder="Nombre completo" value={newCustomer.name} onChange={e => setNewCustomer({...newCustomer, name: e.target.value})} required style={fieldStyle} />
                  </div>
                  <div className="d-flex gap-2 mb-2">
                    <input type="text" className="form-input text-center" placeholder="Teléfono (Opcional)" value={newCustomer.phone} onChange={e => setNewCustomer({...newCustomer, phone: e.target.value})} style={fieldStyle} />
                  </div>
                  <button type="submit" className="btn btn-sm btn-primary">Guardar Cliente</button>
                </form>
              )}
            </div>
          )}
        </div>

        {/* Initial Payment — section title left, toggle right */}
        <div className="mb-4 border" style={cardStyle}>
          <div
            onClick={() => setEnablePayment(!enablePayment)}
            className="d-flex flex-between flex-align-center w-full cursor-pointer"
            style={{ userSelect: 'none' }}
          >
            <span className="font-bold text-primary" style={{ fontSize: '0.95rem', lineHeight: '24px', display: 'inline-block' }}>
              Registrar Abono
            </span>

            {/* Toggle Switch */}
            <div
              style={{
                width: '44px',
                height: '24px',
                backgroundColor: enablePayment ? '#6366f1' : 'rgba(148, 163, 184, 0.3)',
                borderRadius: '12px',
                padding: '2px',
                transition: 'background-color 0.25s ease',
                cursor: 'pointer',
                display: 'inline-flex',
                alignItems: 'center',
                justifyContent: 'flex-start',
                flexShrink: 0
              }}
            >
              <div
                style={{
                  width: '20px',
                  height: '20px',
                  backgroundColor: '#ffffff',
                  borderRadius: '50%',
                  boxShadow: '0 1px 3px rgba(0,0,0,0.3)',
                  transform: enablePayment ? 'translateX(20px)' : 'translateX(0px)',
                  transition: 'transform 0.25s ease'
                }}
              />
            </div>
          </div>

          {enablePayment && (
            <div className="mt-3 pt-3 border-top">
              <div className="form-group mb-2" style={{ textAlign: 'left' }}>
                <label className="form-label" style={{ color: 'var(--text-primary)', fontWeight: '600' }}>Método de Pago</label>
                <select
                  className="form-select text-center"
                  value={paymentMethodId}
                  onChange={(e) => setPaymentMethodId(e.target.value)}
                  style={fieldStyle}
                >
                  {paymentMethods.map(m => (
                    <option key={m.id} value={m.id}>{m.name} {m.isCash ? '(Efectivo)' : ''}</option>
                  ))}
                </select>
              </div>

              <div className="form-group mb-2" style={{ textAlign: 'left' }}>
                <label className="form-label" style={{ color: 'var(--text-primary)', fontWeight: '600' }}>Monto de Abono (Bs.S) - Entrada ATM</label>
                <input
                  type="text"
                  inputMode="numeric"
                  className="form-input font-bold text-center"
                  value={formatNumberEs(initialBsS)}
                  onKeyDown={handleKeyDown}
                  onPaste={handlePaste}
                  onFocus={(e) => { setIsFreshFocus(true); e.target.select(); }}
                  onChange={() => {}}
                  style={fieldStyle}
                />
                {isCashSelected && (
                  <div className="text-muted" style={{ fontSize: '0.75rem', marginTop: '2px', textAlign: 'center', color: 'var(--accent-primary, #6366f1)' }}>
                    El pago en efectivo solo acepta montos enteros.
                  </div>
                )}
                <div className="text-muted" style={{ fontSize: '0.8rem', marginTop: '2px', textAlign: 'center' }}>
                  ≈ {formatUSD(initialUsd)} (Tasa: {formatNumberEs(exchangeRate)} Bs/$)
                </div>
              </div>

              {selectedMethod?.requiresReference && (
                <div className="form-group mb-2" style={{ textAlign: 'left' }}>
                  <label className="form-label" style={{ color: 'var(--text-primary)', fontWeight: '600' }}>Número de Referencia *</label>
                  <input
                    type="text"
                    className="form-input text-center"
                    placeholder="Ingrese el N° de referencia"
                    value={reference}
                    onChange={(e) => setReference(e.target.value)}
                    style={fieldStyle}
                  />
                </div>
              )}
            </div>
          )}
        </div>

        {/* Summary Box — label left, USD center, Bs.S right-aligned */}
        <div className="checkout-summary-box mb-4">
          <div className="checkout-summary-row" style={summaryRowStyle}>
            <span>Total del Pedido:</span>
            <span className="font-bold text-muted">{formatUSD(saleTotalUSD)}</span>
            <span className="font-bold text-nowrap">{formatBsS(saleTotalUSD * exchangeRate)}</span>
          </div>
          {enablePayment && initialBsS > 0 && (
            <div className="checkout-summary-row text-success" style={summaryRowStyle}>
              <span>Abono Inicial:</span>
              <span className="font-bold text-muted">{formatUSD(initialUsd)}</span>
              <span className="font-bold text-nowrap">{formatBsS(initialBsS)}</span>
            </div>
          )}
          <div className="checkout-summary-row highlight" style={summaryRowStyle}>
            <span>Deuda Restante Resultante:</span>
            <span className="font-bold hold-sale-debt text-nowrap">{formatUSD(remainingUsd)}</span>
            <span className="font-bold hold-sale-debt text-nowrap">{formatBsS(remainingBsS)}</span>
          </div>
        </div>

        {/* Footer Actions */}
        <div className="d-flex flex-row flex-align-center justify-center gap-3 w-full mt-4">
          <button
            type="button"
            className="btn btn-outline"
            onClick={onClose}
            disabled={submitting}
            style={{
              height: '42px',
              padding: '0 24px',
              margin: 0,
              lineHeight: 1,
              fontWeight: '700',
              letterSpacing: '0.02em'
            }}
          >
            CANCELAR
          </button>
          <button
            type="button"
            onClick={handleConfirmHold}
            disabled={!selectedCustomer || submitting || (enablePayment && isCashSelected && cents % 100 !== 0)}
            style={{
              height: '42px',
              padding: '0 24px',
              gap: '8px',
              margin: 0,
              lineHeight: 1,
              borderRadius: '8px',
              fontWeight: '700',
              letterSpacing: '0.02em',
              backgroundColor: (!selectedCustomer || submitting) ? 'rgba(148, 163, 184, 0.2)' : '#6366f1',
              color: (!selectedCustomer || submitting) ? '#94a3b8' : '#ffffff',
              border: (!selectedCustomer || submitting) ? '1px solid rgba(148, 163, 184, 0.25)' : '1px solid #6366f1',
              boxShadow: (!selectedCustomer || submitting) ? 'none' : '0 2px 10px rgba(99, 102, 241, 0.45)',
              cursor: (!selectedCustomer || submitting) ? 'not-allowed' : 'pointer',
              opacity: (!selectedCustomer || submitting) ? 0.65 : 1,
              transition: 'all 0.2s ease'
            }}
          >
            {submitting ? <Loader2 className="animate-spin" size={16} /> : <Clock size={16} />}
            <span>CONFIRMAR Y GUARDAR EN ESPERA</span>
          </button>
        </div>
      </div>
    </Modal>
  );
}
