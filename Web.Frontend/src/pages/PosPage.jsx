import { useRef, useState } from 'react';
import { useCartState, useCartActions } from '../context/CartContext';
import { useExchangeRate } from '../context/ExchangeRateContext';
import { usePosHotkeys } from '../hooks/usePosHotkeys';
import { useScannerTrap } from '../hooks/useScannerTrap';
import { usePosModalFlow } from '../hooks/usePosModalFlow';
import { useMobileBackGuard } from '../hooks/useMobileBackGuard';
import SearchBar from '../components/pos/SearchBar';
import EmptyCart from '../components/pos/EmptyCart';
import CartTable from '../components/pos/CartTable';
import CartList from '../components/pos/CartList';
import SummaryPanel from '../components/pos/SummaryPanel';
import CustomerModal from '../components/pos/CustomerModal';
import BarcodeScannerModal from '../components/pos/BarcodeScannerModal';
import VariantSelectorModal from '../components/pos/VariantSelectorModal';
import ConfirmModal from '../components/ui/ConfirmModal';
import { getProductBySku } from '../services/productsApi';
import { isValidBarcode } from '../utils/barcodeValidator';
import { Edit2, ScanLine } from 'lucide-react';

export default function PosPage({
  onOpenCheckout,
  onOpenHold,
  isExternalModalOpen = false,
  onCloseExternalModal,
}) {
  const searchBarRef = useRef(null);
  const abortControllerRef = useRef(null);
  const { exchangeRate, syncBcvRate } = useExchangeRate();
  const [isConfirmClearOpen, setIsConfirmClearOpen] = useState(false);

  const {
    currentSale,
    items,
    selectedItemId,
    setSelectedItemId,
    error,
  } = useCartState();

  const {
    addItem,
    updateQuantity,
    removeItem,
    updateCustomer,
    createNewSale,
    changePriceList,
  } = useCartActions();

  const {
    activeModal,
    isCustomerModalOpen,
    setIsCustomerModalOpen,
    isScannerOpen,
    setIsScannerOpen,
    variantParentProduct,
    setVariantParentProduct,
    handleCloseActiveModal,
  } = usePosModalFlow(isExternalModalOpen, onCloseExternalModal);

  usePosHotkeys({
    activeModal,
    onCloseActiveModal: handleCloseActiveModal,
    onEscapeBackground: () => {
      searchBarRef.current?.clear();
      setSelectedItemId(null);
    },
    onCheckout: () => {
      if (items.length > 0) onOpenCheckout();
    },
    onSearchFocus: () => {
      searchBarRef.current?.focus();
    },
    onChangeCustomer: () => {
      if (currentSale?.id) setIsCustomerModalOpen(true);
    },
    onHold: () => {
      if (items.length > 0) onOpenHold();
    },
    onSyncRate: async () => {
      try {
        await syncBcvRate();
      } catch (err) {
        console.error('[PosPage] Error al sincronizar tasa con F5:', err);
      }
    },
    onTogglePriceList: () => {
      const nextType = currentSale?.priceListType === 'Wholesale' ? 'Retail' : 'Wholesale';
      changePriceList(nextType);
    },
    onClearCart: () => {
      if (items.length > 0) {
        setIsConfirmClearOpen(true);
      }
    },
    onDeleteItem: () => {
      if (selectedItemId) removeItem(selectedItemId);
    },
    onIncreaseQuantity: () => {
      const targetId = selectedItemId || (items.length > 0 ? items[items.length - 1].id : null);
      if (targetId) {
        const item = items.find((i) => i.id === targetId);
        if (item) {
          const step = !item.isFractional ? 1 : (item.unitOfMeasure === 'Grs' || item.unitOfMeasure === 'Ml' ? 100 : item.unitOfMeasure === 'Lb' ? 0.25 : 0.100);
          const newQty = Math.round((item.quantity + step) * 1000) / 1000;
          updateQuantity(item.id, newQty);
        }
      }
    },
    onDecreaseQuantity: () => {
      const targetId = selectedItemId || (items.length > 0 ? items[items.length - 1].id : null);
      if (targetId) {
        const item = items.find((i) => i.id === targetId);
        if (item) {
          const step = !item.isFractional ? 1 : (item.unitOfMeasure === 'Grs' || item.unitOfMeasure === 'Ml' ? 100 : item.unitOfMeasure === 'Lb' ? 0.25 : 0.100);
          const newQty = Math.round((item.quantity - step) * 1000) / 1000;
          if (newQty >= step) {
            updateQuantity(item.id, newQty);
          }
        }
      }
    },
  });

  // Intercepción de navegación "Atrás" en móviles para cerrar modales y prevenir pérdida de carritos
  const {
    isConfirmExitOpen,
    handleCancelExit,
    handleConfirmExit,
  } = useMobileBackGuard({
    activeModal,
    onCloseModal: handleCloseActiveModal,
    hasItems: items.length > 0,
    enabled: true,
  });

  const handleSelectProduct = (product) => {
    if (product?.isGroupHeader) {
      setVariantParentProduct(product);
    } else {
      addItem(product, 1);
    }
  };

  const handleSelectCustomer = async (customerId) => {
    try {
      await updateCustomer(customerId);
    } catch (err) {
      console.error('[PosPage] Error al seleccionar cliente:', err);
    } finally {
      setIsCustomerModalOpen(false);
    }
  };

  const handleScannedCode = async (code) => {
    if (!isValidBarcode(code)) {
      searchBarRef.current?.setQuery('');
      return;
    }

    abortControllerRef.current?.abort();
    const controller = new AbortController();
    abortControllerRef.current = controller;

    try {
      const product = await getProductBySku(code, controller.signal);
      if (controller.signal.aborted) return;

      if (product?.id && !product.isCashAdvance) {
        if (product.isGroupHeader) {
          setVariantParentProduct(product);
        } else {
          await addItem(product, 1);
        }
      }
    } catch (err) {
      if (err?.name === 'AbortError' || controller.signal.aborted) {
        return;
      }
      console.error('[PosPage] Error resolviendo código escaneado:', err);
    } finally {
      if (!controller.signal.aborted) {
        searchBarRef.current?.setQuery('');
      }
    }
  };

  useScannerTrap(handleScannedCode);

  return (
    <div className="pos-page">
      {error && (
        <div className="alert alert-danger" style={{ marginBottom: '1rem' }}>
          {error}
        </div>
      )}

      <div className="d-flex align-items-center justify-content-center gap-2 mb-3 px-1 flex-wrap text-center" style={{ fontSize: '0.875rem', minHeight: '32px' }}>
        <span style={{ fontWeight: '500', color: '#94a3b8', display: 'inline-flex', alignItems: 'center' }}>
          Cliente:&nbsp;
        </span>
        <strong style={{ fontWeight: '700', color: 'var(--text-main, #f8fafc)', letterSpacing: '0.02em', display: 'inline-flex', alignItems: 'center' }}>
          {currentSale?.customerName || 'Consumidor Final'}
        </strong>
        {currentSale?.customerCedula && (
          <span 
            className="badge d-inline-flex align-items-center justify-content-center" 
            style={{ 
              backgroundColor: 'rgba(148, 163, 184, 0.15)', 
              color: 'var(--text-main, #f8fafc)', 
              fontSize: '0.75rem', 
              fontWeight: '600', 
              border: '1px solid rgba(148, 163, 184, 0.3)',
              padding: '3px 10px',
              borderRadius: '12px',
              height: '22px',
              lineHeight: '1'
            }}
          >
            {currentSale.customerCedula}
          </span>
        )}
        <button 
          type="button" 
          className="btn btn-sm d-inline-flex align-items-center justify-content-center gap-1 ms-1"
          style={{ 
            fontSize: '0.75rem', 
            fontWeight: '600', 
            padding: '2px 8px',
            backgroundColor: 'rgba(59, 130, 246, 0.1)',
            color: '#3b82f6',
            border: '1px solid rgba(59, 130, 246, 0.3)',
            borderRadius: '6px'
          }}
          onClick={() => setIsCustomerModalOpen(true)}
          title="Cambiar cliente (F3)"
        >
          <Edit2 size={12} />
          <span>Cambiar (F3)</span>
        </button>
      </div>

      <div className="pos-search-bar mb-3" style={{ display: 'flex', gap: '0.6rem', alignItems: 'stretch', width: '100%' }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <SearchBar
            ref={searchBarRef}
            onSelectProduct={handleSelectProduct}
            exchangeRate={exchangeRate}
            disabled={!currentSale?.id}
            priceListType={currentSale?.priceListType || 'Retail'}
          />
        </div>
        <button
          type="button"
          aria-label="Escanear código de barras con la cámara"
          title="Escanear código de barras con la cámara"
          onClick={() => setIsScannerOpen(true)}
          style={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            width: 48,
            height: 48,
            flexShrink: 0,
            color: 'var(--primary-color, #673AB7)',
            backgroundColor: 'var(--bg-surface, #1e293b)',
            border: '1px solid var(--border, #334155)',
            borderRadius: 'var(--radius-md, 8px)',
            boxShadow: 'var(--shadow-sm, 0 1px 2px rgba(0,0,0,0.1))',
            cursor: 'pointer',
            transition: 'all 0.15s ease',
          }}
        >
          <ScanLine size={20} />
        </button>
      </div>

      <div className="pos-layout">
        <div className="pos-left-column">
          {items.length === 0 ? (
            <EmptyCart />
          ) : (
            <>
              {/* Tabla para Desktop */}
              <div className="desktop-only">
                <CartTable
                  items={items}
                  selectedItemId={selectedItemId}
                  onSelectItem={setSelectedItemId}
                  onUpdateQty={updateQuantity}
                  onUpdateQuantity={updateQuantity}
                  onRemoveItem={removeItem}
                  exchangeRate={exchangeRate}
                />
              </div>
              {/* Lista en Tarjetas para Móvil */}
              <div className="mobile-only">
                <CartList
                  items={items}
                  selectedItemId={selectedItemId}
                  onSelectItem={setSelectedItemId}
                  onUpdateQty={updateQuantity}
                  onUpdateQuantity={updateQuantity}
                  onRemoveItem={removeItem}
                  exchangeRate={exchangeRate}
                />
              </div>
            </>
          )}
        </div>

        <div className="pos-right-column">
          <SummaryPanel
            onCheckout={onOpenCheckout}
            onHold={onOpenHold}
            onOpenScanner={() => setIsScannerOpen(true)}
          />
        </div>
      </div>

      <CustomerModal
        isOpen={isCustomerModalOpen}
        onClose={() => setIsCustomerModalOpen(false)}
        onSelectCustomer={handleSelectCustomer}
        currentCustomerId={currentSale?.customerId}
        mode="select"
        saleTotalUSD={currentSale?.totalUSD || 0}
        exchangeRate={exchangeRate}
      />

      <BarcodeScannerModal
        isOpen={isScannerOpen}
        onClose={() => setIsScannerOpen(false)}
        onCodeScanned={handleScannedCode}
        currentSale={currentSale}
        onUpdateQuantity={updateQuantity}
      />

      <VariantSelectorModal
        isOpen={!!variantParentProduct}
        onClose={() => setVariantParentProduct(null)}
        parentProduct={variantParentProduct}
        onSelectVariant={(variant) => addItem(variant, 1)}
      />

      {/* Modal de confirmación personalizada al intentar salir con productos en el carrito */}
      <ConfirmModal
        isOpen={isConfirmExitOpen}
        onClose={handleCancelExit}
        onConfirm={handleConfirmExit}
        title="¿Abandonar venta actual?"
        message="Tiene productos agregados en el carrito de compras. Si abandona la página ahora, se perderá la venta en curso."
        cancelText="Continuar en POS"
        confirmText="Salir del Sistema"
        variant="warning"
      />

      {/* Modal de confirmación personalizada para limpiar carrito */}
      <ConfirmModal
        isOpen={isConfirmClearOpen}
        onClose={() => setIsConfirmClearOpen(false)}
        onConfirm={() => {
          setIsConfirmClearOpen(false);
          createNewSale();
        }}
        title="¿Limpiar carrito?"
        message="¿Desea limpiar el carrito de compras e iniciar una nueva venta? Esta acción no se puede deshacer."
        cancelText="Cancelar"
        confirmText="Limpiar Carrito"
        variant="warning"
      />
    </div>
  );
}
