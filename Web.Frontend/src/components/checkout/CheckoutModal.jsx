import { useState, useEffect } from 'react';
import Modal from '../ui/Modal';
import PaymentForm from './PaymentForm';
import PaymentList from './PaymentList';
import { getActivePaymentMethods } from '../../services/paymentApi';
import { completeSale } from '../../services/salesApi';
import { useCart } from '../../context/CartContext';
import { useExchangeRate } from '../../context/ExchangeRateContext';
import { useAuth } from '../../context/AuthContext';
import { formatBsS, formatUSD } from '../../utils/formatters';
import { Check, Loader2 } from 'lucide-react';

export default function CheckoutModal({ isOpen, onClose, onSuccess, overrideSale = null, onCompleteSale = null }) {
  const { currentSale, totalBsS: cartTotalBsS, totalUSD: cartTotalUSD, resetCart } = useCart();
  const { exchangeRate } = useExchangeRate();
  const { user } = useAuth();

  const [methods, setMethods] = useState([]);
  const [payments, setPayments] = useState([]);
  const [isPendingPickup, setIsPendingPickup] = useState(false);
  const [isProcessing, setIsProcessing] = useState(false);
  const [error, setError] = useState(null);

  // Determinar la venta a procesar
  const activeSale = overrideSale || currentSale;
  
  // Saldo base a cobrar (en USD)
  const targetTotalUSD = overrideSale 
    ? (overrideSale.remainingBalanceUSD !== undefined ? overrideSale.remainingBalanceUSD : overrideSale.totalUSD)
    : cartTotalUSD;

  // Saldo base en Bs.S usando la tasa vigente del contexto
  const rateToUse = exchangeRate > 0 ? exchangeRate : (activeSale?.appliedRate || 1);
  const targetTotalBsS = overrideSale ? targetTotalUSD * rateToUse : cartTotalBsS;

  // Cargar métodos de pago activos
  useEffect(() => {
    if (isOpen) {
      getActivePaymentMethods()
        .then((res) => setMethods(res || []))
        .catch((err) => console.error('[CheckoutModal] Error al cargar métodos:', err));
      setPayments([]);
      setIsPendingPickup(false);
      setError(null);
    }
  }, [isOpen]);

  const paidBsS = payments.reduce((acc, p) => acc + p.amountBsS, 0);
  const paidUsd = payments.reduce((acc, p) => acc + p.amountUsd, 0);

  const remainingBsS = Math.max(0, targetTotalBsS - paidBsS);
  const remainingUsd = Math.max(0, targetTotalUSD - paidUsd);

  const isFullLiquidation = remainingUsd <= 0.05;

  // Ajuste de redondeo si la diferencia en USD es < 0.01
  const roundingAdjustment = remainingUsd <= 0.01 ? paidBsS - targetTotalBsS : 0;

  // En cobro normal del POS se exige 100% liquidadas. En Cuentas Abiertas (overrideSale) se permiten abonos si hay al menos 1 pago.
  const canFinalize = overrideSale ? (payments.length > 0 || isFullLiquidation) : isFullLiquidation;

  const custName = (activeSale?.customerName || '').toLowerCase();
  const isDefaultCust = !activeSale?.customerId || custName.includes('consumidor final') || custName.includes('general');
  const pendingPickupError = (isPendingPickup && isDefaultCust)
    ? 'Para registrar un apartado pagado (Mercancía en Custodia), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono). Asigne un cliente a la venta antes de continuar.'
    : null;

  const displayError = error || pendingPickupError;

  const handleAddPayment = (newPayment) => {
    setPayments((prev) => [...prev, newPayment]);
  };

  const handleRemovePayment = (index) => {
    setPayments((prev) => prev.filter((_, i) => i !== index));
  };

  const handleFinalizeSale = async () => {
    if (!activeSale?.id || !canFinalize) return;

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
        await onCompleteSale(rawPayments, roundingAdjustment, isPendingPickup);
      } else {
        const invoiceNumber = await completeSale(
          activeSale.id,
          rateToUse,
          rawPayments,
          roundingAdjustment,
          user?.id,
          isPendingPickup
        );

        // Limpiar carrito e iniciar nueva venta (solo en venta normal del POS)
        if (!overrideSale) {
          await resetCart();
        }

        if (onSuccess) {
          onSuccess(invoiceNumber);
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
    <Modal 
      isOpen={isOpen} 
      onClose={onClose} 
      title={overrideSale ? (isFullLiquidation ? "Liquidar Cuenta Completa" : "Liquidar / Registrar Abono a Cuenta") : "Cobranza"} 
      maxWidth="560px"
      centerTitle={true}
    >
      <div className="checkout-summary-box">
        <div className="checkout-summary-row">
          <span>{overrideSale ? "Saldo Pendiente:" : "Total Venta:"}</span>
          <span className="font-bold">{formatBsS(targetTotalBsS || 0)}</span>
        </div>

        <div className="checkout-summary-row text-success">
          <span>Total Pagado Ahora:</span>
          <span className="font-bold">{formatBsS(paidBsS)}</span>
        </div>

        <div className="checkout-summary-row text-danger highlight">
          <span>Restante Tras Cobro:</span>
          <span className="font-bold">{formatBsS(remainingBsS)}</span>
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

      {!overrideSale && (
        <div className="checkout-section" style={{ marginTop: '12px' }}>
          <div
            style={{
              padding: '12px 14px',
              borderRadius: '8px',
              border: isPendingPickup ? '1px solid #f59e0b' : '1px solid var(--border)',
              backgroundColor: isPendingPickup ? 'rgba(245, 158, 11, 0.08)' : 'var(--bg-surface)',
              transition: 'all 0.2s ease'
            }}
          >
            <label className="flex-align-center gap-2 cursor-pointer font-bold" style={{ fontSize: '0.9rem', color: isPendingPickup ? '#f59e0b' : 'var(--text-primary)' }}>
              <input
                type="checkbox"
                checked={isPendingPickup}
                onChange={(e) => setIsPendingPickup(e.target.checked)}
                style={{ width: '18px', height: '18px', cursor: 'pointer' }}
              />
              <span>📦 Mercancía en Custodia (Pendiente por Retirar)</span>
            </label>
            {isPendingPickup && (
              <div className="text-xs text-muted mt-2 pl-6" style={{ lineHeight: '1.4' }}>
                El cliente cancela la factura al 100% en caja y deja los productos resguardados en el local para su retiro posterior. El inventario se descuenta inmediatamente. Se requiere cliente identificado.
              </div>
            )}
          </div>
        </div>
      )}

      <div className="checkout-footer">
        {displayError && (
          <div className="alert alert-danger mb-3" style={{ fontSize: '0.85rem', lineHeight: '1.4', padding: '10px 14px', borderRadius: '8px' }}>
            {displayError}
          </div>
        )}

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
              <Check size={20} /> Cobrar y Finalizar
            </>
          )}
        </button>
      </div>
    </Modal>
  );
}
