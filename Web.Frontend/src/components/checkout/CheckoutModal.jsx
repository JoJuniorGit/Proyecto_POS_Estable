import { useRef, useState, useEffect, useCallback, useMemo, useImperativeHandle, forwardRef } from 'react';

import { bsToUsd } from '../../utils/money.js';
import { createCheckoutKeyHolder } from '../../utils/idempotency.js';
import Modal from '../ui/Modal';
import ConfirmModal from '../ui/ConfirmModal';
import PaymentForm from './PaymentForm';
import PaymentList from './PaymentList';
import { getActivePaymentMethods } from '../../services/paymentApi';
import { completeSale, updateSaleCustomer, getCheckoutPreview } from '../../services/salesApi';
import {
  completeCreditSale,
  extractCreditOverLimitContext,
  getCreditProfile,
  getCreditSaleReceipt,
  isCreditOverLimitError,
} from '../../services/creditApi';
import {
  computeCreditCheckoutState,
  creditDueDate,
  creditOverLimitMessage,
  creditStatusLabel,
  formatDueDateDisplay,
} from '../credit/creditLogic';
import { creditSaleReceiptFileName, saveReceiptBlob } from '../credit/receipts';
import { useCart } from '../../context/CartContext';
import { useExchangeRate } from '../../context/ExchangeRateContext';
import { useAuth } from '../../context/AuthContext';
import { useMonetaryProfile } from '../../context/MonetaryProfileContext';

import { MONETARY_MODE_DUAL } from '../../utils/monetaryProfile';
import CustomerSelectorCard from './CustomerSelectorCard';
import { AlertTriangle, Check, Download, Loader2, PackageCheck, Wallet } from 'lucide-react';
import './CheckoutModal.css';
import '../credit/credit.css';

export function buildCheckoutPreviewRequest({ saleId, exchangeRate, payments, overrideSale }) {
  const previousPayments = overrideSale
    ? (overrideSale.payments || []).map((p) => ({
        paymentMethodId: p.paymentMethodId,
        amount: p.amount || 0,
        amountBsS: p.amountBsS || 0,
      }))
    : [];

  const currentPayments = (payments || []).map((p) => ({
    paymentMethodId: p.methodId,
    amount: p.amountUsd,
    amountBsS: p.amountBsS,
    amountLocal: p.amountBsS,
    referenceNumber: p.reference,
  }));

  return {
    saleId,
    exchangeRate,
    payments: [...previousPayments, ...currentPayments],
  };
}

export function computeCheckoutGate({
  preview,
  previewSignature,
  currentSignature,
  previewFailed,
  hasValidPayments,
  isOverrideSale,
  isDefaultCust,
  isPendingPickup,
}) {
  const isPreviewFresh = preview != null && !previewFailed && previewSignature != null && previewSignature === currentSignature;
  const isFullLiquidation = isPreviewFresh && preview.isFullyPaid === true;
  const isCustodyAllowed = isFullLiquidation && !isDefaultCust;
  const canFinalize = hasValidPayments
    && isPreviewFresh
    && (isOverrideSale ? true : isFullLiquidation)
    && (!isPendingPickup || isCustodyAllowed);
  const roundingAdjustment = isPreviewFresh ? preview.roundingAdjustment : null;

  return { isPreviewFresh, isFullLiquidation, isCustodyAllowed, canFinalize, roundingAdjustment };
}

/**
 * 8.186-T4 (S2/S3): USD EFECTIVO del TOTAL exhibido ("Ref: $") — server-first y sin matemática
 * de política en JS. Prioridad: 1) `effectiveTotalUSD` del preview fresco (misma firma que la
 * petición vigente; T3 garantiza la identidad con el TotalBsS devuelto); 2) `effectiveTotalUSD`
 * del SaleDto del carrito (venta normal, snapshot TotalBsS+AppliedRate); 3) inversa local de
 * display Bs.S/tasa (fallback documentado, solo si el servidor no envía el campo).
 * En cuentas abiertas (overrideSale) lo exhibido es un SALDO PENDIENTE: el USD efectivo del
 * preview/SaleDto corresponde a la venta completa, no al saldo, así que se conserva la inversa
 * local del Bs.S mostrado para no romper la identidad de display.
 * @returns {number} USD efectivo (el render lo fija a 2dp)
 */
export function resolveTotalEffectiveUsd({
  preview,
  isPreviewFresh,
  cartEffectiveTotalUSD,
  overrideSale,
  targetTotalBsS,
  targetTotalUSD,
  rateToUse,
}) {
  if (!overrideSale) {
    const previewEffective = Number(preview?.effectiveTotalUSD);
    if (isPreviewFresh && Number.isFinite(previewEffective) && previewEffective > 0) {
      return previewEffective;
    }

    const cartEffective = Number(cartEffectiveTotalUSD);
    if (Number.isFinite(cartEffective) && cartEffective > 0) {
      return cartEffective;
    }
  }

  return rateToUse > 0 ? bsToUsd(targetTotalBsS, rateToUse) : targetTotalUSD;
}

/**
 * 8.187-T5 (S2/S4/S6): selector puro del modo Crédito del checkout web. Convierte el estado del
 * modal (preview canónico, perfil crediticio y totales exhibidos) en la vista de financiamiento:
 * monto financiado server-first, vencimiento estimado, bloqueos de perfil y sobregiro. En modo
 * Contado queda inerte (sin gates de crédito).
 */
export function resolveCheckoutCreditView({
  isCreditMode,
  hasRegisteredCustomer,
  isCreditProfileLoading,
  creditProfileError,
  creditProfile,
  hasFreshPreview,
  preview,
  fallbackFinancedUSD,
  fallbackFinancedBsS,
}) {
  if (!isCreditMode) {
    return {
      financedUSD: 0,
      financedBsS: 0,
      profileBlockMessage: null,
      isOverLimit: false,
      overLimitMessage: null,
      canFinalize: false,
      dueDate: null,
    };
  }

  const state = computeCreditCheckoutState({
    hasRegisteredCustomer,
    isLoadingProfile: isCreditProfileLoading,
    profileError: creditProfileError,
    profile: creditProfile,
    isPreviewFresh: hasFreshPreview,
    previewRemainingUSD: preview?.remainingBalanceUSD,
    previewRemainingBsS: preview?.remainingBalanceBsS,
    fallbackFinancedUSD,
    fallbackFinancedBsS,
  });

  return {
    ...state,
    dueDate: creditProfile ? creditDueDate(creditProfile.paymentTermDays) : null,
  };
}

const CheckoutModal = forwardRef(function CheckoutModal({ isOpen, onClose, onSuccess, overrideSale = null, onCompleteSale = null }, ref) {
  const { currentSale, totalBsS: cartTotalBsS, totalUSD: cartTotalUSD, effectiveTotalUSD: cartEffectiveTotalUSD, resetCart, updateCustomer } = useCart();
  const { exchangeRate } = useExchangeRate();
  const { user } = useAuth();
  // 8.211-T4b2b1 (S9): los totales grandes usan la moneda local del perfil; la referencia
  // primaria (USD en Dual) solo se muestra cuando el régimen es bimonetario.
  const { mode, primaryCurrencyCode, formatPrimary, formatSecondary } = useMonetaryProfile();
  const isDual = mode === MONETARY_MODE_DUAL;

  const [methods, setMethods] = useState([]);
  const [payments, setPayments] = useState([]);
  const [isPendingPickup, setIsPendingPickup] = useState(false);
  const [isProcessing, setIsProcessing] = useState(false);
  const [error, setError] = useState(null);
  const [selectedSaleCustomer, setSelectedSaleCustomer] = useState(null);
  const [showDiscardConfirm, setShowDiscardConfirm] = useState(false);
  const [preview, setPreview] = useState(null);
  const [previewSignature, setPreviewSignature] = useState(null);
  const [previewFailed, setPreviewFailed] = useState(false);
  // T5 (Paso 11/12): modo de cobro Contado/Crédito y estado del perfil crediticio del cliente.
  const [paymentMode, setPaymentMode] = useState('cash');
  const [creditProfile, setCreditProfile] = useState(null);
  const [creditProfileError, setCreditProfileError] = useState(null);
  const [isCreditProfileLoading, setIsCreditProfileLoading] = useState(false);
  const [creditSuccess, setCreditSuccess] = useState(null);
  const [isDownloadingReceipt, setIsDownloadingReceipt] = useState(false);
  const checkoutKeyHolderRef = useRef(null);
  if (!checkoutKeyHolderRef.current) {
    checkoutKeyHolderRef.current = createCheckoutKeyHolder();
  }

  const handleRequestClose = useCallback(() => {
    // Venta a crédito ya registrada: cerrar solo completa el flujo de éxito (no hay nada que descartar).
    if (creditSuccess) {
      setCreditSuccess(null);
      if (onSuccess) onSuccess(creditSuccess.invoiceNumber, creditSuccess.cartResetOk);
      else onClose?.();
      return true;
    }
    if (payments.length > 0) {
      setShowDiscardConfirm(true);
      return false;
    }
    checkoutKeyHolderRef.current.reset();
    onClose?.();
    return true;
  }, [creditSuccess, onSuccess, onClose, payments.length]);

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
    // T5: reabrir el modal siempre arranca en Contado, sin perfil ni comprobante pendientes.
    setPaymentMode('cash');
    setCreditProfile(null);
    setCreditProfileError(null);
    setIsCreditProfileLoading(false);
    setCreditSuccess(null);
    setIsDownloadingReceipt(false);

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

  // Previsualización canónica del backend: redondeo fiscal, saldo, vuelto y estado de pago total
  const previewRequest = useMemo(
    () => buildCheckoutPreviewRequest({ saleId: activeSale?.id, exchangeRate: rateToUse, payments, overrideSale }),
    [activeSale?.id, rateToUse, payments, overrideSale]
  );
  const previewRequestSignature = useMemo(() => JSON.stringify(previewRequest), [previewRequest]);

  // 8.168 (CLEAN-05-RES): priorizar saldos restantes canónicos de checkout-preview cuando está sincronizado.
  const hasFreshPreview = preview != null && !previewFailed && previewSignature === previewRequestSignature;
  const remainingBsS = hasFreshPreview && typeof preview.remainingBalanceBsS === 'number'
    ? Math.max(0, preview.remainingBalanceBsS)
    : Math.max(0, targetTotalBsS - paidBsS);
  const remainingUsd = hasFreshPreview && typeof preview.remainingBalanceUSD === 'number'
    ? Math.max(0, preview.remainingBalanceUSD)
    : Math.max(0, targetTotalUSD - paidUsd);

  // Sincronización USD efectivo (ANEXO XII / 8.186-T4): el $ de referencia del TOTAL es el
  // inverso del Bs.S EFECTIVO que se cobra (no la suma canónica de precios base), tomado del
  // servidor (preview fresco → SaleDto del carrito) con inversa local solo como fallback de
  // display. Gates y saldos canónicos intactos.
  const effectiveTotalUsd = resolveTotalEffectiveUsd({
    preview,
    isPreviewFresh: hasFreshPreview,
    cartEffectiveTotalUSD,
    overrideSale,
    targetTotalBsS,
    targetTotalUSD,
    rateToUse,
  });
  const effectiveRemainingUsd = rateToUse > 0 ? bsToUsd(remainingBsS, rateToUse) : remainingUsd;

  useEffect(() => {
    if (!isOpen || !previewRequest.saleId || creditSuccess) {
      setPreview(null);
      setPreviewSignature(null);
      return;
    }

    let cancelled = false;
    setPreviewFailed(false);
    getCheckoutPreview(previewRequest.saleId, previewRequest.exchangeRate, previewRequest.payments)
      .then((res) => {
        if (!cancelled) {
          if (!res) {
            setPreviewFailed(true);
            setPreview(null);
            setPreviewSignature(null);
          } else {
            setPreviewFailed(false);
            setPreview(res);
            setPreviewSignature(previewRequestSignature);
          }
        }
      })
      .catch((err) => {
        console.error('[CheckoutModal] Error al obtener previsualización de cobro:', err);
        if (!cancelled) {
          setPreviewFailed(true);
          setPreview(null);
          setPreviewSignature(null);
        }
      });

    return () => { cancelled = true; };
  }, [isOpen, previewRequest, previewRequestSignature, creditSuccess]);

  const hasValidPayments = payments.length > 0 && payments.every((p) => (p.amountBsS > 0 || p.amountUsd > 0));

  const custName = (activeSale?.customerName || '').toLowerCase();
  const isDefaultCust = !activeSale?.customerId || activeSale?.customer?.isDefault || custName.includes('consumidor final') || custName.includes('general');
  const hasRegisteredCustomer = Boolean(activeSale?.customerId) && !isDefaultCust;

  // 8.5-WEB1: Zero-trust en el redondeo fiscal. El ajuste canónico proviene EXCLUSIVAMENTE del
  // preview del backend; sin fallback local aproximado que pueda cerrar con vuelto distinto.
  // Venta normal POS: requiere al menos 1 pago y saldo cubierto. Cuentas Abiertas (overrideSale): permite abonos parciales con al menos 1 pago.
  const {
    isFullLiquidation,
    isCustodyAllowed,
    canFinalize,
    roundingAdjustment,
  } = computeCheckoutGate({
    preview,
    previewSignature,
    currentSignature: previewRequestSignature,
    previewFailed,
    hasValidPayments,
    isOverrideSale: !!overrideSale,
    isDefaultCust,
    isPendingPickup,
  });

  // T5 (S2/S4): modo Crédito solo en ventas nuevas del POS (las cuentas abiertas no se financian).
  const isCreditMode = paymentMode === 'credit' && !overrideSale;

  const creditView = resolveCheckoutCreditView({
    isCreditMode,
    hasRegisteredCustomer,
    isCreditProfileLoading,
    creditProfileError,
    creditProfile,
    hasFreshPreview,
    preview,
    fallbackFinancedUSD: effectiveRemainingUsd,
    fallbackFinancedBsS: remainingBsS,
  });

  // T5 (S1/S4): perfil crediticio del cliente registrado cuando se activa el modo Crédito.
  // El disponible, la mora y la utilización son cálculos del servidor: acá solo se consumen.
  useEffect(() => {
    if (!isOpen || !isCreditMode || creditSuccess || !activeSale?.customerId || isDefaultCust) {
      setCreditProfile(null);
      setCreditProfileError(null);
      setIsCreditProfileLoading(false);
      return undefined;
    }

    let cancelled = false;
    const customerId = activeSale.customerId;
    setIsCreditProfileLoading(true);
    setCreditProfileError(null);
    getCreditProfile(customerId)
      .then((profile) => {
        if (!cancelled) setCreditProfile(profile);
      })
      .catch((err) => {
        console.error('[CheckoutModal] Error al cargar el perfil crediticio:', err);
        if (!cancelled) {
          setCreditProfile(null);
          setCreditProfileError(`No se pudo cargar el perfil crediticio: ${err.message}`);
        }
      })
      .finally(() => {
        if (!cancelled) setIsCreditProfileLoading(false);
      });

    return () => { cancelled = true; };
  }, [isOpen, isCreditMode, creditSuccess, activeSale?.customerId, isDefaultCust]);

  const effectiveIsPendingPickup = isCreditMode ? false : (isPendingPickup && isCustodyAllowed);

  const handleSelectCustomer = async (cust) => {
    if (!cust?.id) return;
    if (overrideSale) {
      const updated = await updateSaleCustomer(overrideSale.id, cust.id);
      setSelectedSaleCustomer(updated);
    } else {
      await updateCustomer(cust.id);
    }
  };

  // T5 (S1/S4): escritura administrativa del perfil desde la búsqueda de clientes del POS no
  // aplica acá: el modo Crédito solo lee el perfil y bloquea la venta si no es elegible.
  const pendingPickupError = (!isCreditMode && isPendingPickup && isDefaultCust)
    ? 'Para registrar un apartado pagado (Mercancía en Custodia), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono). Use el selector de la parte superior.'
    : null;

  const noPaymentsNotice = isCreditMode
    ? (previewFailed
        ? 'No se pudo validar el financiamiento con el servidor. Verifique la conexión e intente nuevamente (la transacción no puede cerrarse sin la validación canónica).'
        : null)
    : (!hasValidPayments
        ? 'Agregue al menos un método de pago presionando "+ Agregar Pago" para procesar el cobro.'
        : (previewFailed
            ? 'No se pudo validar el cobro con el servidor. Verifique la conexión e intente nuevamente (la transacción no puede cerrarse sin la validación canónica).'
            : (!overrideSale && !isFullLiquidation ? 'El monto acumulado aún no cubre el 100% del total de la venta.' : null)));

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

  const buildRawPayments = () => payments.map((p) => ({
    paymentMethodId: p.methodId,
    amount: p.amountUsd,
    amountBsS: p.amountBsS,
    amountLocal: p.amountBsS,
    referenceNumber: p.reference,
  }));

  /**
   * T5 (S2/S4/S5): cierre a crédito. Solo se envía el abono inicial (si existe); el remanente
   * queda como Cuenta por Cobrar y solo lo cobrado ingresa a caja. El 403 de sobregiro se
   * traduce a un mensaje claro que pide la autorización presencial desde una caja WPF.
   */
  const handleFinalizeCreditSale = async () => {
    if (!activeSale?.id || !creditView.canFinalize) {
      setError('Revise el perfil crediticio y el monto financiado antes de continuar.');
      return;
    }

    setIsProcessing(true);
    setError(null);

    const saleId = activeSale.id;
    const financed = {
      financedUSD: creditView.financedUSD,
      financedBsS: creditView.financedBsS,
      dueDate: creditView.dueDate,
    };

    try {
      const invoiceNumber = await completeCreditSale(saleId, {
        exchangeRate: rateToUse,
        payments: buildRawPayments(),
        cashierId: user?.id,
      });

      checkoutKeyHolderRef.current.reset();

      // Limpiar carrito e iniciar nueva venta (mismo contrato del cierre de contado).
      let cartResetOk = true;
      if (!overrideSale) {
        cartResetOk = await resetCart();
        if (!cartResetOk) {
          console.warn('[CheckoutModal] Venta a crédito registrada, pero el carrito no pudo iniciar una nueva venta.');
        }
      }

      setCreditSuccess({
        saleId,
        invoiceNumber,
        cartResetOk,
        paidNowUsd: paidUsd,
        paidNowBsS: paidBsS,
        ...financed,
      });
    } catch (err) {
      console.error('[CheckoutModal] Error al registrar la venta a crédito:', err);
      if (isCreditOverLimitError(err)) {
        const context = extractCreditOverLimitContext(err);
        setError(creditOverLimitMessage({
          financedUSD: context?.financedUSD ?? creditView.financedUSD,
          availableCreditUSD: context?.availableCreditUSD ?? creditProfile?.availableCreditUSD ?? 0,
        }));
      } else {
        setError(err.message || 'Ocurrió un error al registrar la venta a crédito.');
      }
    } finally {
      setIsProcessing(false);
    }
  };

  const handleFinalizeSale = async () => {
    if (isCreditMode) {
      await handleFinalizeCreditSale();
      return;
    }

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
      const rawPayments = buildRawPayments();

      if (onCompleteSale) {
        await onCompleteSale(rawPayments, roundingAdjustment, effectiveIsPendingPickup, checkoutKeyHolderRef.current.getOrCreateKey(), isFullLiquidation);
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

  const handleDownloadCreditReceipt = async () => {
    if (!creditSuccess?.saleId) return;

    setIsDownloadingReceipt(true);
    setError(null);
    try {
      const blob = await getCreditSaleReceipt(creditSuccess.saleId);
      saveReceiptBlob(blob, creditSaleReceiptFileName(creditSuccess.invoiceNumber, creditSuccess.saleId));
    } catch (err) {
      console.error('[CheckoutModal] Error al descargar el comprobante de venta a crédito:', err);
      setError(err.message || 'No se pudo descargar el comprobante de venta a crédito.');
    } finally {
      setIsDownloadingReceipt(false);
    }
  };

  const finalizeDisabled = isProcessing
    || (isCreditMode
      ? !creditView.canFinalize
      : (!canFinalize || (isPendingPickup && isDefaultCust)));

  // T5 (S5): el comprobante de venta a crédito se ofrece apenas la venta queda registrada, antes
  // de cerrar el modal (mismo texto de factura que el resto del POS).
  if (creditSuccess) {
    return (
      <Modal
        isOpen={isOpen}
        onClose={handleRequestClose}
        title="Venta a Crédito Registrada"
        maxWidth="560px"
      >
        <div className="credit-success-panel">
          <div className="credit-success-badge">
            Factura N° {Number(creditSuccess.invoiceNumber) > 0 ? String(creditSuccess.invoiceNumber).padStart(6, '0') : creditSuccess.saleId}
          </div>
          <p className="credit-success-note">
            La venta quedó registrada como Cuenta por Cobrar: el inventario se descontó y solo el abono inicial ingresó a caja.
          </p>

          <dl className="credit-summary-list">
            <div className="credit-summary-row">
              <dt>Monto financiado:</dt>
              <dd>
                <strong>{formatPrimary(creditSuccess.financedUSD)}</strong>
                {isDual && <span className="text-muted"> ({formatSecondary(creditSuccess.financedBsS)})</span>}
              </dd>
            </div>
            <div className="credit-summary-row">
              <dt>Abono inicial cobrado:</dt>
              <dd>{formatPrimary(creditSuccess.paidNowUsd)} {isDual && <span className="text-muted">({formatSecondary(creditSuccess.paidNowBsS)})</span>}</dd>
            </div>
            <div className="credit-summary-row">
              <dt>Vencimiento estimado:</dt>
              <dd>{formatDueDateDisplay(creditSuccess.dueDate)}</dd>
            </div>
          </dl>

          {error && (
            <div className="alert alert-danger mb-3 chk-alert-note">
              <AlertTriangle size={16} /> {error}
            </div>
          )}

          <button
            type="button"
            className="btn btn-outline btn-block"
            onClick={handleDownloadCreditReceipt}
            disabled={isDownloadingReceipt}
          >
            {isDownloadingReceipt
              ? <><Loader2 className="animate-spin" size={18} /> Generando comprobante...</>
              : <><Download size={18} /> Descargar Comprobante de Venta a Crédito</>}
          </button>

          <button
            type="button"
            className="btn btn-primary btn-block btn-lg mt-2"
            onClick={handleRequestClose}
          >
            <Check size={20} /> Listo
          </button>
        </div>
      </Modal>
    );
  }

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

      {/* T5 (S2): modalidad de cobro — las cuentas abiertas (overrideSale) siguen siendo contado/abono. */}
      {!overrideSale && (
        <div className="checkout-section">
          <div className="checkout-mode-toggle" role="group" aria-label="Modalidad de cobro">
            <button
              type="button"
              className={`btn ${paymentMode === 'cash' ? 'btn-primary' : 'btn-outline'}`}
              onClick={() => setPaymentMode('cash')}
              disabled={isProcessing}
              aria-pressed={paymentMode === 'cash'}
            >
              Contado
            </button>
            <button
              type="button"
              className={`btn ${paymentMode === 'credit' ? 'btn-primary' : 'btn-outline'}`}
              onClick={() => setPaymentMode('credit')}
              disabled={isProcessing}
              aria-pressed={paymentMode === 'credit'}
            >
              <Wallet size={16} /> Crédito (CxC)
            </button>
          </div>
        </div>
      )}

      <div className="checkout-summary-box">
        <div className="checkout-summary-row">
          <span>{overrideSale ? "Saldo Pendiente:" : "Total Venta:"}</span>
          <div className="text-right">
            <div className="font-bold color-primary chk-font-total">{formatSecondary(targetTotalBsS || 0)}</div>
            {isDual && (
              <div className="text-xs text-muted font-medium">Ref: {formatPrimary(effectiveTotalUsd)} {primaryCurrencyCode}</div>
            )}
          </div>
        </div>

        <div className="checkout-summary-row text-success">
          <span>Total Pagado Ahora:</span>
          <div className="text-right">
            <div className="font-bold chk-font-paid">{formatSecondary(paidBsS)}</div>
            {isDual && (
              <div className="text-xs text-muted font-medium">Ref: {formatPrimary(paidUsd)} {primaryCurrencyCode}</div>
            )}
          </div>
        </div>

        <div className="checkout-summary-row text-danger highlight">
          <span>Restante Tras Cobro:</span>
          <div className="text-right">
            <div className="font-bold chk-font-remaining">{formatSecondary(remainingBsS)}</div>
            {isDual && (
              <div className="text-xs text-muted font-medium">Ref: {formatPrimary(effectiveRemainingUsd)} {primaryCurrencyCode}</div>
            )}
          </div>
        </div>
      </div>

      {isCreditMode && (
        <div className="checkout-section credit-checkout-panel">
          <h4 className="credit-checkout-title">
            <Wallet size={16} /> Crédito del cliente
          </h4>
          <dl className="credit-detail-grid">
            <div className="credit-detail-row">
              <dt>Cliente:</dt>
              <dd>{hasRegisteredCustomer ? (activeSale?.customer?.name || activeSale?.customerName) : 'Sin cliente registrado'}</dd>
            </div>
            <div className="credit-detail-row">
              <dt>Estado:</dt>
              <dd>{creditProfile ? creditStatusLabel(creditProfile.creditStatus) : (isCreditProfileLoading ? 'Consultando...' : '—')}</dd>
            </div>
            <div className="credit-detail-row">
              <dt>Crédito disponible:</dt>
              <dd>{creditProfile ? formatPrimary(creditProfile.availableCreditUSD) : '—'}</dd>
            </div>
            <div className="credit-detail-row">
              <dt>Monto a financiar:</dt>
              <dd>
                <strong data-testid="credit-financed-usd">{formatPrimary(creditView.financedUSD)}</strong>
                {isDual && <span className="text-muted"> ({formatSecondary(creditView.financedBsS)})</span>}
              </dd>
            </div>
            <div className="credit-detail-row">
              <dt>Vencimiento estimado:</dt>
              <dd data-testid="credit-due-date">{formatDueDateDisplay(creditView.dueDate)}</dd>
            </div>
            <div className="credit-detail-row">
              <dt>Plazo / gracia:</dt>
              <dd>{creditProfile ? `${creditProfile.paymentTermDays} días / ${creditProfile.creditGraceDays} días` : '—'}</dd>
            </div>
          </dl>

          {creditView.overLimitMessage && (
            <div className="alert alert-danger mt-2 chk-alert-note" role="alert">
              <AlertTriangle size={16} /> {creditView.overLimitMessage}
            </div>
          )}
          {!creditView.overLimitMessage && creditView.profileBlockMessage && (
            <div className="alert alert-warning mt-2 chk-alert-note" role="status">
              {creditView.profileBlockMessage}
            </div>
          )}
          {!hasValidPayments && !creditView.overLimitMessage && !creditView.profileBlockMessage && (
            <div className="text-xs text-muted mt-2 chk-info-note">
              Crédito total: se financia el 100% de la venta. Cargue un pago inicial para registrar un crédito mixto.
            </div>
          )}
        </div>
      )}

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

      {!isCreditMode && (
      <div className="checkout-section mt-3">
        <div className={`chk-pickup-box${isPendingPickup ? ' chk-pickup-box--pending' : ''}${(!isFullLiquidation || isDefaultCust) ? ' chk-pickup-box--disabled' : ''}`}
        >
          <label className={`flex-align-center gap-2 cursor-pointer font-bold chk-pickup-label${isPendingPickup ? ' chk-pickup-label--pending' : ''}`}>
            <input
              type="checkbox"
              checked={isPendingPickup}
              disabled={!isFullLiquidation || isDefaultCust}
              onChange={(e) => setIsPendingPickup(e.target.checked)}
              className={`chk-pickup-checkbox${(!isFullLiquidation || isDefaultCust) ? ' chk-pickup-checkbox--disabled' : ''}`}
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
      )}

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
          disabled={finalizeDisabled}
          onClick={handleFinalizeSale}
        >
          {isProcessing ? (
            <>
              <Loader2 className="animate-spin" size={20} /> Procesando...
            </>
          ) : (
            <>
              {isCreditMode ? (
                <>
                  <Wallet size={20} /> Registrar Venta a Crédito
                </>
              ) : effectiveIsPendingPickup ? (
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
      message={isDual
        ? `Tiene ${payments.length} pago(s) registrado(s) por un monto de ${formatPrimary(paidUsd)} ${primaryCurrencyCode} (${formatSecondary(paidBsS)}). Si sale ahora, se descartarán los pagos ingresados.`
        : `Tiene ${payments.length} pago(s) registrado(s) por un monto de ${formatSecondary(paidBsS)}. Si sale ahora, se descartarán los pagos ingresados.`}
      cancelText="Continuar Cobro"
      confirmText="Descartar y Salir"
      variant="danger"
    />
  </>
  );
});

export default CheckoutModal;

