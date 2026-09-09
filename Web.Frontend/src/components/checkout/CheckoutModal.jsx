import { useRef, useState, useEffect, useCallback, useImperativeHandle, forwardRef } from 'react';

import { createCheckoutKeyHolder } from '../../utils/idempotency.js';
import Modal from '../ui/Modal';
import ConfirmModal from '../ui/ConfirmModal';
import PaymentForm from './PaymentForm';
import PaymentList from './PaymentList';
import { getActivePaymentMethods } from '../../services/paymentApi';
import { completeSale, updateSaleCustomer, getCheckoutPreview } from '../../services/salesApi';
import { useCart } from '../../context/CartContext';
import { useExchangeRate } from '../../context/ExchangeRateContext';
import { useAuth } from '../../context/AuthContext';
import { formatBsS } from '../../utils/formatters';
import CustomerSelectorCard from './CustomerSelectorCard';
import { Check, Loader2, PackageCheck } from 'lucide-react';
import './CheckoutModal.css';

const CheckoutModal = forwardRef(function CheckoutModal({ isOpen, onClose, onSuccess, overrideSale = null, onCompleteSale = null }, ref) {
  const { currentSale, totalBsS: cartTotalBsS, totalUSD: cartTotalUSD, resetCart, updateCustomer } = useCart();
  const { exchangeRate } = useExchangeRate();
  const { user } = useAuth();

  const [methods, setMethods] = useState([]);
  const [payments, setPayments] = useState([]);
  const [isPendingPickup, setIsPendingPickup] = useState(false);
  const [isProcessing, setIsProcessing] = useState(false);
  const [error, setError] = useState(null);
  const [selectedSaleCustomer, setSelectedSaleCustomer] = useState(null);
  const [showDiscardConfirm, setShowDiscardConfirm] = useState(false);
  const [preview, setPreview] = useState(null);
  const [previewFailed, setPreviewFailed] = useState(false);
  const checkoutKeyHolderRef = useRef(null);
  if (!checkoutKeyHolderRef.current) {
    checkoutKeyHolderRef.current = createCheckoutKeyHolder();
  }

  const handleRequestClose = useCallback(() => {
    if (payments.length > 0) {
      setShowDiscardConfirm(true);
      return false;
    }
    checkoutKeyHolderRef.current.reset();
    onClose?.();
    return true;
  }, [payments.length, onClose]);

  useImperativeHandle(ref, () => ({
    requestClose: handleRequestClose,
    hasPayments: payments.length > 0,
  }), [handleRequestClose, payments.length]);

  // Determinar la venta a procesar (priorizando actualización local en la sesión de cobro)
  const activeSale = selectedSaleCustomer || overrideSale || currentSale;
  
  // Saldo base a cobrar (en USD)
  const targetTotalUSD = overrideSale 
    ? (overrideSale.remainingBalanceUSD !== undefined ? overrideSale.remainingBalanceUSD : Math.max(0, (overrideSale.totalUSD || 0) - (overrideSale.totalPaidUSD || 0)))
    : cartTotalUSD;

  // Saldo base en Bs.S usando la tasa vigente del contexto y el total real de la venta
  const rateToUse = exchangeRate > 0 ? exchangeRate : (activeSale?.appliedRate || 1);
  const paidPreviousBsS = overrideSale 
    ? (overrideSale.payments || []).reduce((acc, p) => acc + (p.amountBsS > 0 ? p.amountBsS : (p.amount || 0) * (p.exchangeRate || rateToUse)), 0) 
    : 0;
  const targetTotalBsS = overrideSale 
    ? Math.max(0, (overrideSale.totalBsS !== undefined && overrideSale.totalBsS > 0 ? overrideSale.totalBsS : targetTotalUSD * rateToUse) - paidPreviousBsS)
    : cartTotalBsS;

  // Cargar métodos de pago activos
  useEffect(() => {
    if (!isOpen) return;
    let cancelled = false;
    setPayments([]);
    setIsPendingPickup(false);
    setError(null);
    setSelectedSaleCustomer(null);

    // 8.5-WEB3: reintento único ante fallo transitorio de payment-methods; error visible si persiste.
    const loadMethods = (attempt) => {
      getActivePaymentMethods()
        .then((res) => {
          if (cancelled) return;
          setMethods(res || []);
        })
        .catch((err) => {
          console.error('[CheckoutModal] Error al cargar métodos:', err);
          if (cancelled) return;
          if (attempt < 1) {
            setTimeout(() => loadMethods(attempt + 1), 800);
          } else {
            setError('No se pudieron cargar los métodos de pago. Verifique la conexión e intente nuevamente.');
          }
        });
    };
    loadMethods(0);

    return () => { cancelled = true; };
  }, [isOpen]);

  const paidBsS = payments.reduce((acc, p) => acc + p.amountBsS, 0);
  const paidUsd = payments.reduce((acc, p) => acc + p.amountUsd, 0);

  const remainingBsS = Math.max(0, targetTotalBsS - paidBsS);
  const remainingUsd = Math.max(0, targetTotalUSD - paidUsd);

  // Previsualización canónica del backend: redondeo fiscal, saldo, vuelto y estado de pago total
  useEffect(() => {
    if (!isOpen || !activeSale?.id) {
      setPreview(null);
      return;
    }

    const prevPayments = overrideSale
      ? (overrideSale.payments || []).map((p) => ({
          paymentMethodId: p.paymentMethodId,
          amount: p.amount || 0,
          amountBsS: p.amountBsS || 0,
        }))
      : [];

    const newPayments = payments.map((p) => ({
      paymentMethodId: p.methodId,
      amount: p.amountUsd,
      amountBsS: p.amountBsS,
      amountLocal: p.amountBsS,
      referenceNumber: p.reference,
    }));

    let cancelled = false;
    setPreviewFailed(false);
    getCheckoutPreview(activeSale.id, rateToUse, [...prevPayments, ...newPayments])
      .then((res) => {
        if (!cancelled) {
          if (!res) {
            setPreviewFailed(true);
            setPreview(null);
          } else {
            setPreviewFailed(false);
            setPreview(res);
          }
        }
      })
      .catch((err) => {
        console.error('[CheckoutModal] Error al obtener previsualización de cobro:', err);
        if (!cancelled) {
          setPreviewFailed(true);
          setPreview(null);
        }
      });

    return () => { cancelled = true; };
  }, [isOpen, activeSale?.id, overrideSale, payments, rateToUse]);

  const hasValidPayments = payments.length > 0 && payments.every((p) => (p.amountBsS > 0 || p.amountUsd > 0));
  const isFullLiquidation = preview?.isFullyPaid ?? (hasValidPayments && remainingUsd <= 0.05);

  // 8.5-WEB1: Zero-trust en el redondeo fiscal. El ajuste canónico proviene EXCLUSIVAMENTE del
  // preview del backend; sin fallback local aproximado que pueda cerrar con vuelto distinto.
  const roundingAdjustment = preview?.roundingAdjustment ?? 0;

  const custName = (activeSale?.customerName || '').toLowerCase();
  const isDefaultCust = !activeSale?.customerId || activeSale?.customer?.isDefault || custName.includes('consumidor final') || custName.includes('general');

  const isCustodyAllowed = isFullLiquidation && !isDefaultCust;
  const effectiveIsPendingPickup = isPendingPickup && isCustodyAllowed;

  // Venta normal POS: requiere al menos 1 pago y saldo cubierto. Cuentas Abiertas (overrideSale): permite abonos parciales con al menos 1 pago.
  // 8.5-WEB1: Se requiere preview canónico del backend (redondeo fiscal/vuelto/saldo) para finalizar.
  const canFinalize = hasValidPayments && !previewFailed && (overrideSale ? true : isFullLiquidation) && (!isPendingPickup || isCustodyAllowed);

  const handleSelectCustomer = async (cust) => {
    if (!cust?.id) return;
    if (overrideSale) {
      const updated = await updateSaleCustomer(overrideSale.id, cust.id);
      setSelectedSaleCustomer(updated);
    } else {
      await updateCustomer(cust.id);
    }
  };

  const pendingPickupError = (isPendingPickup && isDefaultCust)
    ? 'Para registrar un apartado pagado (Mercancía en Custodia), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono). Use el selector de la parte superior.'
    : null;

  const noPaymentsNotice = !hasValidPayments
    ? 'Agregue al menos un método de pago presionando "+ Agregar Pago" para procesar el cobro.'
    : (previewFailed
        ? 'No se pudo validar el cobro con el servidor. Verifique la conexión e intente nuevamente (la transacción no puede cerrarse sin la validación canónica).'
        : (!overrideSale && !isFullLiquidation ? 'El monto acumulado aún no cubre el 100% del total de la venta.' : null));

  const displayError = error || pendingPickupError;

  const handleAddPayment = (newPayment) => {
    setPayments((prev) => [...prev, newPayment]);
  };

  const handleRemovePayment = (targetPayment) => {
    // 8.5-WEB5: remover por uid estable (la key del PaymentList ya no es el índice).
    setPayments((prev) =>
      targetPayment?.uid
        ? prev.filter((p) => p.uid !== targetPayment.uid)
        : prev.filter((p) => p !== targetPayment)
    );
  };

  const handleFinalizeSale = async () => {
    if (!activeSale?.id || !canFinalize || !hasValidPayments) {
      setError('Debe agregar al menos un método de pago con monto válido antes de continuar.');
      return;
    }

    // Validación de Cliente para Mercancía en Custodia (Pendiente por Retirar)
    if (isPendingPickup && isDefaultCust) {
      setError('Para registrar un apartado pagado (Mercancía en Custodia), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono). Asigne un cliente a la venta antes de continuar.');
      return;
    }

    setIsProcessing(true);
    setError(null);

    try {
      const rawPayments = payments.map((p) => ({
        paymentMethodId: p.methodId,
        amount: p.amountUsd,
        amountBsS: p.amountBsS,
        amountLocal: p.amountBsS,
        referenceNumber: p.reference,
      }));

      if (onCompleteSale) {
        await onCompleteSale(rawPayments, roundingAdjustment, effectiveIsPendingPickup, checkoutKeyHolderRef.current.getOrCreateKey());
        checkoutKeyHolderRef.current.reset();
      } else {
        const invoiceNumber = await completeSale(
          activeSale.id,
          rateToUse,
          rawPayments,
          roundingAdjustment,
          user?.id,
          effectiveIsPendingPickup,
          checkoutKeyHolderRef.current.getOrCreateKey()
        );

        checkoutKeyHolderRef.current.reset();

        // Limpiar carrito e iniciar nueva venta (solo en venta normal del POS)
        let cartResetOk = true;
        if (!overrideSale) {
          // 8.5-WEB5: se honra el booleano de resetCart(); si la nueva venta no pudo iniciarse,
          // se notifica al caller para que no anuncie éxito con un currentSale obsoleto.
          cartResetOk = await resetCart();
          if (!cartResetOk) {
            console.warn('[CheckoutModal] Venta liquidada, pero el carrito no pudo iniciar una nueva venta.');
          }
        }

        if (onSuccess) {
          onSuccess(invoiceNumber, cartResetOk);
        }
      }
    } catch (err) {
      console.error('[CheckoutModal] Error al completar venta:', err);
      setError(err.message || 'Ocurrió un error al procesar la transacción.');
    } finally {
      setIsProcessing(false);
    }
  };

  return (
    <>
      <Modal 
        isOpen={isOpen} 
        onClose={handleRequestClose} 
        title={overrideSale ? (isFullLiquidation ? "Liquidar Cuenta Completa" : "Liquidar / Registrar Abono a Cuenta") : "Cobranza"} 
        maxWidth="560px"
      >
      <CustomerSelectorCard
        currentCustomer={activeSale?.customer || (activeSale?.customerName ? { id: activeSale.customerId, name: activeSale.customerName, cedulaOrRif: activeSale.customerCedula } : null)}
        isPendingPickup={effectiveIsPendingPickup}
        onSelectCustomer={handleSelectCustomer}
        disabled={isProcessing}
        readOnly={!!overrideSale}
      />

      <div className="checkout-summary-box">
        <div className="checkout-summary-row">
          <span>{overrideSale ? "Saldo Pendiente:" : "Total Venta:"}</span>
          <div className="text-right">
            <div className="font-bold color-primary chk-font-total">{formatBsS(targetTotalBsS || 0)}</div>
            <div className="text-xs text-muted font-medium">Ref: ${targetTotalUSD.toFixed(2)} USD</div>
          </div>
        </div>

        <div className="checkout-summary-row text-success">
          <span>Total Pagado Ahora:</span>
          <div className="text-right">
            <div className="font-bold chk-font-paid">{formatBsS(paidBsS)}</div>
            <div className="text-xs text-muted font-medium">Ref: ${paidUsd.toFixed(2)} USD</div>
          </div>
        </div>

        <div className="checkout-summary-row text-danger highlight">
          <span>Restante Tras Cobro:</span>
          <div className="text-right">
            <div className="font-bold chk-font-remaining">{formatBsS(remainingBsS)}</div>
            <div className="text-xs text-muted font-medium">Ref: ${remainingUsd.toFixed(2)} USD</div>
          </div>
        </div>
      </div>

      <div className="checkout-section">
        <PaymentForm
          methods={methods}
          remainingBsS={remainingBsS}
          exchangeRate={rateToUse}
          onAddPayment={handleAddPayment}
        />
      </div>

      <div className="checkout-section">
        <PaymentList payments={payments} onRemovePayment={handleRemovePayment} />
      </div>

      <div className="checkout-section mt-3">
        <div className="chk-pickup-box"
          style={{
            border: isPendingPickup ? '1px solid #f59e0b' : '1px solid var(--border)',
            backgroundColor: isPendingPickup ? 'rgba(245, 158, 11, 0.08)' : 'var(--bg-surface)',
            opacity: (!isFullLiquidation || isDefaultCust) ? 0.8 : 1
          }}
        >
          <label className="flex-align-center gap-2 cursor-pointer font-bold chk-pickup-label" style={{ color: isPendingPickup ? '#f59e0b' : 'var(--text-primary)' }}>
            <input
              type="checkbox"
              checked={isPendingPickup}
              disabled={!isFullLiquidation || isDefaultCust}
              onChange={(e) => setIsPendingPickup(e.target.checked)}
              className="chk-pickup-checkbox"
              style={{ cursor: (!isFullLiquidation || isDefaultCust) ? 'not-allowed' : 'pointer' }}
            />
            <span>📦 Mercancía en Custodia (Pendiente por Retirar)</span>
          </label>

          {!isFullLiquidation ? (
            <div className="text-xs text-warning mt-2 pl-6 chk-warning-note">
              ⚠️ Requiere pagar el 100% de la venta para poder enviar a Retiros Pendientes.
            </div>
          ) : isDefaultCust ? (
            <div className="text-xs text-warning mt-2 pl-6 chk-warning-note">
              ⚠️ Requiere seleccionar un cliente real (Nombre, Cédula y Teléfono) para activar la entrega posterior.
            </div>
          ) : isPendingPickup && (
            <div className="text-xs text-muted mt-2 pl-6 chk-info-note">
              El cliente cancela la factura al 100% en caja y deja los productos resguardados en el local para su retiro posterior. El inventario se descuenta inmediatamente.
            </div>
          )}
        </div>
      </div>

      <div className="checkout-footer">
        {displayError ? (
          <div className="alert alert-danger mb-3 chk-alert-note">
            {displayError}
          </div>
        ) : (noPaymentsNotice && (
          <div className="alert alert-warning mb-3 chk-alert-note">
            {noPaymentsNotice}
          </div>
        ))}

        <button
          type="button"
          className="btn btn-primary btn-lg btn-block"
          disabled={!canFinalize || isProcessing || (isPendingPickup && isDefaultCust)}
          onClick={handleFinalizeSale}
        >
          {isProcessing ? (
            <>
              <Loader2 className="animate-spin" size={20} /> Procesando...
            </>
          ) : (
            <>
              {effectiveIsPendingPickup ? (
                <>
                  <PackageCheck size={20} /> Cobrar y Enviar a Retiros Pendientes
                </>
              ) : overrideSale ? (
                isFullLiquidation ? (
                  <>
                    <Check size={20} /> Liquidar Cuenta
                  </>
                ) : (
                  <>
                    <Check size={20} /> Registrar Abono
                  </>
                )
              ) : (
                <>
                  <Check size={20} /> Cobrar y Finalizar
                </>
              )}
            </>
          )}
        </button>
      </div>
    </Modal>

    {/* Confirmación personalizada si intenta salir con al menos 1 pago registrado */}
    <ConfirmModal
      isOpen={showDiscardConfirm}
      onClose={() => setShowDiscardConfirm(false)}
      onConfirm={() => {
        setShowDiscardConfirm(false);
        setPayments([]);
        checkoutKeyHolderRef.current.reset();
        onClose?.();
      }}
      title="¿Cancelar cobro en curso?"
      message={`Tiene ${payments.length} pago(s) registrado(s) por un monto de $${paidUsd.toFixed(2)} USD (${formatBsS(paidBsS)}). Si sale ahora, se descartarán los pagos ingresados.`}
      cancelText="Continuar Cobro"
      confirmText="Descartar y Salir"
      variant="danger"
    />
  </>
  );
});

export default CheckoutModal;
