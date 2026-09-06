import { useState, useEffect, useRef } from 'react';
import { AuthProvider, useAuth } from './context/AuthContext';
import { ExchangeRateProvider, useExchangeRate } from './context/ExchangeRateContext';
import { CurrencyFormatProvider } from './context/CurrencyFormatContext';
import { CartProvider, useCart } from './context/CartContext';
import Layout from './components/layout/Layout';
import LoginPage from './pages/LoginPage';
import PosPage from './pages/PosPage';
import CatalogPage from './pages/CatalogPage';
import HistoryPage from './pages/HistoryPage';
import PendingOrdersPage from './pages/PendingOrdersPage';
import PendingPickupsPage from './pages/PendingPickupsPage';
import RegisterPage from './pages/RegisterPage';
import RegisterClosePage from './pages/RegisterClosePage';
import SettingsPage from './pages/SettingsPage';
import ExchangeRatePage from './pages/ExchangeRatePage';
import CheckoutModal from './components/checkout/CheckoutModal';
import HoldSaleModal from './components/pos/HoldSaleModal';
import SuccessScreen from './components/checkout/SuccessScreen';

const VALID_VIEWS = ['pos', 'catalog', 'history', 'pending', 'pickups', 'register', 'closing', 'settings', 'exchange'];

function getInitialView() {
  const hash = window.location.hash.replace('#', '').trim();
  if (hash && VALID_VIEWS.includes(hash)) {
    return hash;
  }
  const saved = localStorage.getItem('pos_active_view');
  if (saved && VALID_VIEWS.includes(saved)) {
    return saved;
  }
  return 'pos';
}

function MainApp() {
  const { isAuthenticated } = useAuth();
  const [currentView, setCurrentView] = useState(getInitialView);
  const [isCheckoutOpen, setIsCheckoutOpen] = useState(false);
  const [isHoldModalOpen, setIsHoldModalOpen] = useState(false);
  const [completedInvoice, setCompletedInvoice] = useState(null);
  const [completedHoldSuccess, setCompletedHoldSuccess] = useState(null);
  const checkoutRef = useRef(null);

  const { exchangeRate, isRateOutdated } = useExchangeRate();
  const { currentSale, totalUSD, totalBsS, resetCart } = useCart();

  useEffect(() => {
    function handleHashChange() {
      const hash = window.location.hash.replace('#', '').trim();
      if (hash && VALID_VIEWS.includes(hash)) {
        setCurrentView(hash);
        localStorage.setItem('pos_active_view', hash);
      }
    }
    window.addEventListener('hashchange', handleHashChange);
    return () => window.removeEventListener('hashchange', handleHashChange);
  }, []);

  const handleNavigate = (view) => {
    setCurrentView(view);
    localStorage.setItem('pos_active_view', view);
    window.location.hash = view;
  };

  const handleCheckoutSuccess = (invoiceNumber) => {
    setIsCheckoutOpen(false);
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

  const handleCloseHoldSuccess = () => {
    setCompletedHoldSuccess(null);
    handleNavigate('pending');
  };

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
    switch (currentView) {
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
        return (
          <PosPage
            onOpenCheckout={() => setIsCheckoutOpen(true)}
            onOpenHold={() => setIsHoldModalOpen(true)}
            isExternalModalOpen={isExternalModalOpen}
            onCloseExternalModal={handleCloseExternalModal}
          />
        );
    }
  };

  if (!isAuthenticated) {
    return <LoginPage />;
  }

  return (
    <Layout
      currentView={currentView}
      onNavigate={handleNavigate}
      exchangeRate={exchangeRate}
      isRateOutdated={isRateOutdated}
    >
      {renderView()}

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
          onClose={() => setCompletedInvoice(null)}
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
