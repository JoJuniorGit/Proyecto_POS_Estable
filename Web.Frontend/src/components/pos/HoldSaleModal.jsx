import React, { useState, useEffect, useRef } from 'react';
import Modal from '../ui/Modal';
import { getCustomers, createCustomer } from '../../services/customerApi';
import { getActivePaymentMethods } from '../../services/paymentApi';
import { holdSale } from '../../services/salesApi';
import { formatNumberEs, formatBsS, formatUSD } from '../../utils/formatters';
import { Search, UserPlus, Clock, Loader2, RefreshCw, X } from 'lucide-react';

export default function HoldSaleModal({ isOpen, onClose, saleId, currentCustomer, saleTotalUSD, exchangeRate, onSuccess }) {
  const [query, setQuery] = useState('');
  const [customers, setCustomers] = useState([]);
  const [loadingCustomers, setLoadingCustomers] = useState(false);
  const [selectedCustomer, setSelectedCustomer] = useState(null);
  const searchInputRef = useRef(null);

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
      setQuery('');
    }
  }, [isOpen, currentCustomer]);

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
    loadCustomers(val);
  };

  const handleClearOrChangeCustomer = () => {
    setSelectedCustomer(null);
    setQuery('');
    loadCustomers('');
    setTimeout(() => {
      if (searchInputRef.current) searchInputRef.current.focus();
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
      loadCustomers(created.cedulaOrRif);
    } catch (err) {
      setError(err.response?.data || err.message || 'Error al crear cliente');
    }
  };

  // ATM Input Handlers for Initial Payment
  const initialBsS = cents / 100;
  const initialUsd = exchangeRate > 0 ? initialBsS / exchangeRate : 0;
  const remainingUsd = Math.max(0, saleTotalUSD - (enablePayment ? initialUsd : 0));
  const remainingBsS = remainingUsd * exchangeRate;

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
      const initialPaymentsList = (enablePayment && initialBsS > 0) ? [{
        paymentMethodId: parseInt(paymentMethodId, 10),
        amountBsS: initialBsS,
        amountUSD: parseFloat(initialUsd.toFixed(2)),
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
      {error && <div className="alert alert-danger mb-3 text-center">{error}</div>}

      {/* Step 1: Customer Selection */}
      <div className="mb-4">
        {selectedCustomer ? (
          <div 
            className="p-3 rounded-3 border text-center"
            style={{ backgroundColor: 'var(--bg-surface)', borderColor: 'var(--border)' }}
          >
            <div className="d-flex justify-content-between align-items-center mb-2">
              <span style={{ fontSize: '0.8rem', color: 'var(--text-muted)', fontWeight: '600', textTransform: 'uppercase' }}>
                Cliente Asignado
              </span>
              <button 
                type="button" 
                className="btn btn-sm btn-link p-0 text-decoration-none"
                style={{ fontSize: '0.8rem', color: 'var(--accent-primary)', fontWeight: '600' }}
                onClick={handleClearOrChangeCustomer}
              >
                <RefreshCw size={13} className="me-1" /> Cambiar Cliente
              </button>
            </div>
            
            <div className="font-bold" style={{ color: 'var(--text-primary)', fontSize: '1.05rem' }}>
              {selectedCustomer.name}
            </div>
            <div style={{ color: 'var(--text-muted)', fontSize: '0.85rem', marginTop: '2px' }}>
              {selectedCustomer.cedulaOrRif} {selectedCustomer.phone ? `• ${selectedCustomer.phone}` : ''}
            </div>
          </div>
        ) : (
          <div>
            <label className="form-label font-bold mb-2" style={{ fontSize: '0.95rem', color: 'var(--text-primary)' }}>
              1. Cliente de la Cuenta (Requerido)
            </label>
            <div style={{ display: 'flex', flexDirection: 'row', alignItems: 'center', gap: '8px', marginBottom: '8px', width: '100%' }}>
              <div style={{ position: 'relative', flex: '1 1 auto', minWidth: 0 }}>
                <input
                  ref={searchInputRef}
                  type="text"
                  className="form-input text-center"
                  placeholder="Buscar por Nombre o Cédula/RIF..."
                  value={query}
                  onChange={handleSearchChange}
                  style={{ 
                    paddingLeft: '36px', 
                    paddingRight: '36px', 
                    height: '38px',
                    backgroundColor: 'var(--bg-input)', 
                    color: 'var(--text-primary)', 
                    borderColor: 'var(--border)' 
                  }}
                />
                <Search size={16} style={{ position: 'absolute', left: '12px', top: '50%', transform: 'translateY(-50%)', color: 'var(--text-muted)' }} />
                {query && (
                  <button 
                    type="button" 
                    onClick={() => { setQuery(''); loadCustomers(''); }}
                    style={{ position: 'absolute', right: '10px', top: '50%', transform: 'translateY(-50%)', background: 'none', border: 'none', color: 'var(--text-muted)', cursor: 'pointer' }}
                  >
                    <X size={14} />
                  </button>
                )}
              </div>
              <button 
                type="button" 
                className="btn btn-outline d-inline-flex align-items-center justify-content-center gap-1.5"
                onClick={() => setIsCreatingCustomer(!isCreatingCustomer)}
                style={{ 
                  height: '38px',
                  flexShrink: 0,
                  borderColor: 'var(--border)', 
                  color: 'var(--text-primary)', 
                  whiteSpace: 'nowrap' 
                }}
              >
                <UserPlus size={16} /> Crear Cliente
              </button>
            </div>

            {isCreatingCustomer && (
              <form 
                onSubmit={handleCreateCustomer} 
                className="p-3 rounded-3 border mb-3 text-center"
                style={{ backgroundColor: 'var(--bg-surface)', borderColor: 'var(--border)' }}
              >
                <div className="font-bold mb-2 text-center" style={{ color: 'var(--text-primary)' }}>Registrar Nuevo Cliente</div>
                <div className="d-flex gap-2 mb-2">
                  <input type="text" className="form-input text-center" placeholder="Cédula/RIF" value={newCustomer.cedulaOrRif} onChange={e => setNewCustomer({...newCustomer, cedulaOrRif: e.target.value})} required style={{ backgroundColor: 'var(--bg-input)', color: 'var(--text-primary)', borderColor: 'var(--border)' }} />
                  <input type="text" className="form-input text-center" placeholder="Nombre completo" value={newCustomer.name} onChange={e => setNewCustomer({...newCustomer, name: e.target.value})} required style={{ backgroundColor: 'var(--bg-input)', color: 'var(--text-primary)', borderColor: 'var(--border)' }} />
                </div>
                <div className="d-flex gap-2 mb-2">
                  <input type="text" className="form-input text-center" placeholder="Teléfono (Opcional)" value={newCustomer.phone} onChange={e => setNewCustomer({...newCustomer, phone: e.target.value})} style={{ backgroundColor: 'var(--bg-input)', color: 'var(--text-primary)', borderColor: 'var(--border)' }} />
                </div>
                <button type="submit" className="btn btn-sm btn-primary">Guardar Cliente</button>
              </form>
            )}

            {/* List box styled for Dark and Light mode + Custom Scrollbar + Left Aligned Flex Rows */}
            <div 
              className="border rounded-3 custom-scrollbar" 
              style={{ 
                maxHeight: '220px', 
                overflowY: 'auto', 
                backgroundColor: 'var(--bg-surface)', 
                borderColor: 'var(--border)' 
              }}
            >
              {loadingCustomers ? (
                <div className="p-3 text-center" style={{ color: 'var(--text-muted)' }}>
                  <Loader2 className="animate-spin d-inline me-2" size={16} /> Buscando clientes...
                </div>
              ) : customers.length === 0 ? (
                <div className="p-3 text-center" style={{ color: 'var(--text-muted)', fontSize: '0.875rem' }}>
                  No se encontraron clientes registrados disponibles.
                </div>
              ) : (
                (!query.trim() ? customers.slice(0, 3) : customers).map(c => {
                  const isItemChosen = selectedCustomer?.id === c.id;
                  return (
                    <div
                      key={c.id}
                      className="p-2.5 px-3 border-bottom d-flex align-items-center justify-content-between text-start cursor-pointer customer-item-row"
                      onClick={() => setSelectedCustomer(c)}
                      style={{ 
                        cursor: 'pointer',
                        borderBottomColor: 'var(--border)',
                        backgroundColor: isItemChosen ? 'rgba(99, 102, 241, 0.15)' : 'transparent',
                        borderLeft: isItemChosen ? '4px solid var(--accent-primary, #6366f1)' : '4px solid transparent',
                        transition: 'all 0.15s ease',
                        width: '100%'
                      }}
                      onMouseEnter={(e) => {
                        if (!isItemChosen) e.currentTarget.style.backgroundColor = 'var(--bg-hover, rgba(255, 255, 255, 0.05))';
                      }}
                      onMouseLeave={(e) => {
                        if (!isItemChosen) e.currentTarget.style.backgroundColor = 'transparent';
                      }}
                    >
                      {/* Lado Izquierdo: Nombre (con truncate) y datos personales */}
                      <div style={{ flex: '1 1 auto', minWidth: 0, paddingRight: '12px' }}>
                        <strong 
                          style={{ 
                            fontSize: '0.925rem', 
                            color: 'var(--text-primary)', 
                            display: 'block', 
                            overflow: 'hidden', 
                            textOverflow: 'ellipsis', 
                            whiteSpace: 'nowrap' 
                          }}
                          title={c.name}
                        >
                          {c.name}
                        </strong>
                        <div style={{ fontSize: '0.8rem', color: 'var(--text-muted)', marginTop: '2px' }}>
                          {c.cedulaOrRif} {c.phone ? `• ${c.phone}` : ''}
                        </div>
                      </div>

                    </div>
                  );
                })
              )}
            </div>
          </div>
        )}
      </div>

      {/* Step 2: Initial Payment with Centered Toggle Switch */}
      <div 
        className="mb-4 p-3 rounded-3 border text-center"
        style={{ backgroundColor: 'var(--bg-surface)', borderColor: 'var(--border)' }}
      >
        <div 
          className="d-flex align-items-center justify-content-center gap-3 cursor-pointer text-center"
          onClick={() => setEnablePayment(!enablePayment)}
          style={{ userSelect: 'none', margin: '0 auto', width: 'fit-content' }}
        >
          <span className="font-bold" style={{ fontSize: '0.95rem', color: 'var(--text-primary)', lineHeight: '24px', display: 'inline-block' }}>
            Registrar Abono
          </span>

          {/* Toggle Switch */}
          <div 
            style={{
              width: '44px',
              height: '24px',
              backgroundColor: enablePayment ? 'var(--accent-primary, #6366f1)' : 'rgba(148, 163, 184, 0.3)',
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
          <div className="mt-3 pt-3 border-top" style={{ borderTopColor: 'var(--border)' }}>
            <div className="form-group mb-2 text-start">
              <label className="form-label" style={{ color: 'var(--text-primary)', fontWeight: '600' }}>Método de Pago</label>
              <select
                className="form-select text-center"
                value={paymentMethodId}
                onChange={(e) => setPaymentMethodId(e.target.value)}
                style={{ backgroundColor: 'var(--bg-input)', color: 'var(--text-primary)', borderColor: 'var(--border)' }}
              >
                {paymentMethods.map(m => (
                  <option key={m.id} value={m.id}>{m.name} {m.isCash ? '(Efectivo)' : ''}</option>
                ))}
              </select>
            </div>

            <div className="form-group mb-2 text-start">
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
                style={{ backgroundColor: 'var(--bg-input)', color: 'var(--text-primary)', borderColor: 'var(--border)' }}
              />
              <div style={{ fontSize: '0.8rem', color: 'var(--text-muted)', marginTop: '2px', textAlign: 'center' }}>
                ≈ {formatUSD(initialUsd)} (Tasa: {formatNumberEs(exchangeRate)} Bs/$)
              </div>
            </div>

            {selectedMethod?.requiresReference && (
              <div className="form-group mb-2 text-start">
                <label className="form-label" style={{ color: 'var(--text-primary)', fontWeight: '600' }}>Número de Referencia *</label>
                <input
                  type="text"
                  className="form-input text-center"
                  placeholder="Ingrese el N° de referencia"
                  value={reference}
                  onChange={(e) => setReference(e.target.value)}
                  style={{ backgroundColor: 'var(--bg-input)', color: 'var(--text-primary)', borderColor: 'var(--border)' }}
                />
              </div>
            )}
          </div>
        )}
      </div>

      {/* Summary Box */}
      <div className="checkout-summary-box mb-4">
        <div className="checkout-summary-row">
          <span>Total del Pedido:</span>
          <span className="font-bold">{formatBsS(saleTotalUSD * exchangeRate)} ({formatUSD(saleTotalUSD)})</span>
        </div>
        {enablePayment && initialBsS > 0 && (
          <div className="checkout-summary-row text-success">
            <span>Abono Inicial:</span>
            <span className="font-bold">{formatBsS(initialBsS)} ({formatUSD(initialUsd)})</span>
          </div>
        )}
        <div className="checkout-summary-row text-danger highlight">
          <span>Deuda Restante Resultante:</span>
          <span className="font-bold">{formatBsS(remainingBsS)} ({formatUSD(remainingUsd)})</span>
        </div>
      </div>

      {/* Footer Actions */}
      <div 
        style={{ 
          display: 'flex', 
          flexDirection: 'row', 
          alignItems: 'center', 
          justifyContent: 'center', 
          gap: '12px', 
          width: '100%', 
          marginTop: '16px' 
        }}
      >
        <button 
          type="button" 
          className="btn btn-outline" 
          onClick={onClose} 
          disabled={submitting}
          style={{ 
            height: '42px', 
            padding: '0 24px', 
            display: 'inline-flex', 
            alignItems: 'center', 
            justifyContent: 'center',
            margin: 0,
            lineHeight: 1
          }}
        >
          Cancelar
        </button>
        <button
          type="button"
          className="btn btn-primary"
          onClick={handleConfirmHold}
          disabled={!selectedCustomer || submitting}
          style={{ 
            height: '42px', 
            padding: '0 24px', 
            display: 'inline-flex', 
            alignItems: 'center', 
            justifyContent: 'center',
            gap: '8px',
            margin: 0,
            lineHeight: 1,
            borderRadius: '8px',
            fontWeight: '700',
            backgroundColor: (!selectedCustomer || submitting) ? 'rgba(148, 163, 184, 0.2)' : 'var(--accent-primary, #6366f1)',
            color: (!selectedCustomer || submitting) ? '#94a3b8' : '#ffffff',
            border: (!selectedCustomer || submitting) ? '1px solid rgba(148, 163, 184, 0.25)' : '1px solid var(--accent-primary, #6366f1)',
            boxShadow: (!selectedCustomer || submitting) ? 'none' : '0 2px 8px rgba(99, 102, 241, 0.4)',
            cursor: (!selectedCustomer || submitting) ? 'not-allowed' : 'pointer',
            opacity: (!selectedCustomer || submitting) ? 0.65 : 1,
            transition: 'all 0.2s ease'
          }}
        >
          {submitting ? <Loader2 className="animate-spin" size={16} /> : <Clock size={16} />}
          <span>CONFIRMAR Y GUARDAR EN ESPERA</span>
        </button>
      </div>
    </Modal>
  );
}
