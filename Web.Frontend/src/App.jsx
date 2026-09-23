import { useState, useEffect, useRef, lazy, Suspense, useCallback } from 'react';
import { AuthProvider, useAuth } from './context/AuthContext';
import { ExchangeRateProvider, useExchangeRate } from './context/ExchangeRateContext';
import { CurrencyFormatProvider } from './context/CurrencyFormatContext';
import { CartProvider, useCart } from './context/CartContext';
import Layout from './components/layout/Layout';
import LoginPage from './pages/LoginPage';
import CheckoutModal from './components/checkout/CheckoutModal';
import HoldSaleModal from './components/pos/HoldSaleModal';
import SuccessScreen from './components/checkout/SuccessScreen';
import FullScreenLoader from './components/ui/FullScreenLoader';
import SaleRecoveryModal from './components/pos/SaleRecoveryModal';
import { useShutdownGuard } from './hooks/useShutdownGuard';
import { hasOpenModals } from './utils/modalRegistry';
import { isValidView, resolveAccessibleView } from './navigation/roleViews';
import AccessDenied from './navigation/AccessDenied';

// 8.6-M5: code splitting — cada página se carga como chunk propio (React.lazy).
const PosPage = lazy(() => import('./pages/PosPage'));
const CatalogPage = lazy(() => import('./pages/CatalogPage'));
const HistoryPage = lazy(() => import('./pages/HistoryPage'));
const PendingOrdersPage = lazy(() => import('./pages/PendingOrdersPage'));
const PendingPickupsPage = lazy(() => import('./pages/PendingPickupsPage'));
const RegisterPage = lazy(() => import('./pages/RegisterPage'));
const RegisterClosePage = lazy(() => import('./pages/RegisterClosePage'));
const SettingsPage = lazy(() => import('./pages/SettingsPage'));
const ExchangeRatePage = lazy(() => import('./pages/ExchangeRatePage'));

function getInitialView() {
  const hash = window.location.hash.replace('#', '').trim();
  if (hash && isValidView(hash)) {
    return hash;
  }
  const saved = localStorage.getItem('pos_active_view');
  if (saved && isValidView(saved)) {
    return saved;
  }
  return 'pos';
}

function MainApp() {
  const { isAuthenticated, user } = useAuth();
  const [currentView, setCurrentView] = useState(getInitialView);
  const [isCheckoutOpen, setIsCheckoutOpen] = useState(false);
  const [isHoldModalOpen, setIsHoldModalOpen] = useState(false);
  const [completedInvoice, setCompletedInvoice] = useState(null);
  const [completedHoldSuccess, setCompletedHoldSuccess] = useState(null);
  const checkoutRef = useRef(null);

  const { exchangeRate, isRateOutdated } = useExchangeRate();
  const { currentSale, totalUSD, totalBsS, resetCart, items, pendingRecovery, recoveryError, recoveryProcessing, recoverPendingSale, discardPendingRecovery, flushSaleState } = useCart();

  const viewAccess = resolveAccessibleView(user?.role, currentView);
  const activeView = viewAccess.allowed ? currentView : viewAccess.view;

  useEffect(() => {
    if (!isAuthenticated) return;
    const resolved = resolveAccessibleView(user?.role, currentView);
    if (!resolved.allowed && resolved.view) {
      setCurrentView(resolved.view);
      localStorage.setItem('pos_active_view', resolved.view);
    }
  }, [isAuthenticated, user?.role, currentView]);

  useEffect(() => {
    function handleHashChange() {
      const hash = window.location.hash.replace('#', '').trim();
      if (isValidView(hash)) {
        setCurrentView(hash);
        localStorage.setItem('pos_active_view', hash);
      }
    }
    window.addEventListener('hashchange', handleHashChange);
    return () => window.removeEventListener('hashchange', handleHashChange);
  }, []);

  const handleNavigate = useCallback((view) => {
    setCurrentView(view);
    localStorage.setItem('pos_active_view', view);
    window.location.hash = view;
  }, []);

  const handleCheckoutSuccess = async (invoiceNumber, cartResetOk = true) => {
    setIsCheckoutOpen(false);
    // 8.5-WEB5: si el CheckoutModal no logró iniciar una nueva venta, se re-intenta aquí antes de
    // anunciar el éxito (evita dejar el carrito con la venta ya liquidada).
    if (!cartResetOk) {
      const ok = await resetCart();
      if (!ok) {
        console.warn('[App] No se pudo iniciar la nueva venta tras la liquidación.');
      }
    }
    setCompletedInvoice(invoiceNumber);
  };

  const handleHoldSuccess = async () => {
    const saleId = currentSale?.id;
    setIsHoldModalOpen(false);
    const ok = await resetCart();
    if (!ok) {
      // 8.5-WEB5: no anunciar éxito si el carrito no pudo iniciar una nueva venta.
      setCompletedHoldSuccess({
        title: "Pedido Guardado en Espera",
        badgeText: saleId ? `Pedido N° #${saleId}` : null,
        message: "El pedido fue guardado correctamente, pero no se pudo iniciar una nueva venta automáticamente. Intente crear una nueva venta manualmente o recargue la página."
      });
      return;
    }
    setCompletedHoldSuccess({
      title: "¡Pedido Guardado en Espera!",
      badgeText: saleId ? `Pedido N° #${saleId}` : null,
      message: "El pedido fue asignado al cliente y guardado en espera correctamente."
    });
  };

  const handleCloseHoldSuccess = useCallback(() => {
    setCompletedHoldSuccess(null);
    handleNavigate('pending');
  }, [handleNavigate]);

  const handleCloseCompletedInvoice = useCallback(() => {
    setCompletedInvoice(null);
  }, []);

  const isExternalModalOpen = isCheckoutOpen || isHoldModalOpen || Boolean(completedInvoice) || Boolean(completedHoldSuccess);

  const handleCloseExternalModal = () => {
    if (isCheckoutOpen) {
      if (checkoutRef.current) {
        const closed = checkoutRef.current.requestClose();
        if (closed === false) return false;
      }
      setIsCheckoutOpen(false);
      return true;
    }
    if (isHoldModalOpen) {
      setIsHoldModalOpen(false);
      return true;
    }
    if (completedInvoice) {
      setCompletedInvoice(null);
      return true;
    }
    if (completedHoldSuccess) {
      setCompletedHoldSuccess(null);
      return true;
    }
    return false;
  };

  const renderView = () => {
    if (!activeView) {
      return <AccessDenied />;
    }
    switch (activeView) {
      case 'pos':
        return (
          <PosPage
            onOpenCheckout={() => setIsCheckoutOpen(true)}
            onOpenHold={() => setIsHoldModalOpen(true)}
            isExternalModalOpen={isExternalModalOpen}
            onCloseExternalModal={handleCloseExternalModal}
          />
        );
      case 'catalog':
        return <CatalogPage />;
      case 'history':
        return <HistoryPage />;
      case 'pending':
        return <PendingOrdersPage onNavigate={handleNavigate} />;
      case 'pickups':
        return <PendingPickupsPage />;
      case 'register':
        return <RegisterPage />;
      case 'closing':
        return <RegisterClosePage />;
      case 'settings':
        return <SettingsPage />;
      case 'exchange':
        return <ExchangeRatePage />;
      default:
        return <AccessDenied />;
    }
  };

  const getHasVolatileState = useCallback(() => (
    items.length > 0 ||
    isCheckoutOpen ||
    isHoldModalOpen ||
    Boolean(completedInvoice) ||
    Boolean(completedHoldSuccess) ||
    Boolean(pendingRecovery) ||
    Boolean(checkoutRef.current?.hasPayments) ||
    hasOpenModals()
  ), [items.length, isCheckoutOpen, isHoldModalOpen, completedInvoice, completedHoldSuccess, pendingRecovery]);

  useShutdownGuard({ getHasVolatileState, flushState: flushSaleState });

  if (!isAuthenticated) {
    return <LoginPage />;
  }

  return (
    <Layout
      currentView={activeView}
      onNavigate={handleNavigate}
      exchangeRate={exchangeRate}
      isRateOutdated={isRateOutdated}
    >
      <Suspense fallback={<FullScreenLoader />}>
        {renderView()}
      </Suspense>

      {/* Modal de Checkout / Cobro */}
      <CheckoutModal
        ref={checkoutRef}
        isOpen={isCheckoutOpen}
        onClose={() => setIsCheckoutOpen(false)}
        onSuccess={handleCheckoutSuccess}
      />

      {/* Modal de Guardar en Espera */}
      <HoldSaleModal
        isOpen={isHoldModalOpen}
        onClose={() => setIsHoldModalOpen(false)}
        saleId={currentSale?.id}
        currentCustomer={currentSale?.customer || (currentSale?.customerName && currentSale?.customerName !== 'Consumidor Final' ? { id: currentSale.customerId, name: currentSale.customerName, cedulaOrRif: currentSale.customerCedula, creditLimitUSD: currentSale.customerCreditLimitUSD || 0 } : null)}
        saleTotalUSD={totalUSD}
        saleTotalBsS={totalBsS}
        exchangeRate={exchangeRate}
        onSuccess={handleHoldSuccess}
      />

      {/* Overlay de Éxito / Confirmación de Factura */}
      {completedInvoice && (
        <SuccessScreen
          invoiceNumber={completedInvoice}
          onClose={handleCloseCompletedInvoice}
        />
      )}

      {/* Overlay de Éxito / Confirmación de Guardado en Espera */}
      {completedHoldSuccess && (
        <SuccessScreen
          type="hold"
          title={completedHoldSuccess.title}
          badgeText={completedHoldSuccess.badgeText}
          message={completedHoldSuccess.message}
          buttonText="Ver Cuentas Abiertas"
          onClose={handleCloseHoldSuccess}
        />
      )}

      <SaleRecoveryModal
        isOpen={Boolean(pendingRecovery)}
        recovery={pendingRecovery}
        isProcessing={recoveryProcessing}
        error={recoveryError}
        onRecover={recoverPendingSale}
        onDiscard={discardPendingRecovery}
      />
    </Layout>
  );
}

export default function App() {
  return (
    <AuthProvider>
      <ExchangeRateProvider>
        <CurrencyFormatProvider>
          <CartProvider>
            <MainApp />
          </CartProvider>
        </CurrencyFormatProvider>
      </ExchangeRateProvider>
    </AuthProvider>
  );
}
