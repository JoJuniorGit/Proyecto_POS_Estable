import { useRef, useState, useMemo } from 'react';
import { useCart } from '../context/CartContext';
import { useExchangeRate } from '../context/ExchangeRateContext';
import { usePosHotkeys } from '../hooks/usePosHotkeys';
import { useScannerTrap } from '../hooks/useScannerTrap';
import SearchBar from '../components/pos/SearchBar';
import EmptyCart from '../components/pos/EmptyCart';
import CartTable from '../components/pos/CartTable';
import CartList from '../components/pos/CartList';
import SummaryPanel from '../components/pos/SummaryPanel';
import CustomerModal from '../components/pos/CustomerModal';
import BarcodeScannerModal from '../components/pos/BarcodeScannerModal';
import VariantSelectorModal from '../components/pos/VariantSelectorModal';
import { getProductBySku } from '../services/productsApi';
import { isValidBarcode } from '../utils/barcodeValidator';
import { Edit2, ScanLine, Keyboard } from 'lucide-react';

export default function PosPage({
  onOpenCheckout,
  onOpenHold,
  isExternalModalOpen = false,
  onCloseExternalModal,
}) {
  const [isCustomerModalOpen, setIsCustomerModalOpen] = useState(false);
  const [isScannerOpen, setIsScannerOpen] = useState(false);
  const [variantParentProduct, setVariantParentProduct] = useState(null);
  const searchBarRef = useRef(null);
  const abortControllerRef = useRef(null);
  const { exchangeRate, syncBcvRate } = useExchangeRate();
  const {
    currentSale,
    items,
    selectedItemId,
    setSelectedItemId,
    addItem,
    updateQuantity,
    removeItem,
    updateCustomer,
    createNewSale,
    changePriceList,
    error,
  } = useCart();

  const activeModal = isExternalModalOpen
    ? 'external'
    : isCustomerModalOpen
    ? 'customer'
    : isScannerOpen
    ? 'scanner'
    : variantParentProduct
    ? 'variant'
    : null;

  usePosHotkeys({
    activeModal,
    onCloseActiveModal: () => {
      if (variantParentProduct) setVariantParentProduct(null);
      else if (isCustomerModalOpen) setIsCustomerModalOpen(false);
      else if (isScannerOpen) setIsScannerOpen(false);
      else if (onCloseExternalModal) onCloseExternalModal();
    },
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
      if (items.length > 0 && window.confirm('¿Desea limpiar el carrito e iniciar una nueva venta?')) {
        createNewSale();
      }
    },
    onDeleteItem: () => {
      if (selectedItemId) removeItem(selectedItemId);
    },
    onIncreaseQuantity: () => {
      if (selectedItemId) {
        const item = items.find((i) => i.id === selectedItemId);
        if (item) updateQuantity(item.id, item.quantity + 1);
      }
    },
    onDecreaseQuantity: () => {
      if (selectedItemId) {
        const item = items.find((i) => i.id === selectedItemId);
        if (item && item.quantity > 1) updateQuantity(item.id, item.quantity - 1);
      }
    },
  });

  const handleSelectProduct = (product) => {
    if (product?.isGroupHeader) {
      setVariantParentProduct(product);
    } else {
      addItem(product, 1);
    }
  };

  const handleSelectCustomer = async (customerId) => {
    await updateCustomer(customerId);
    setIsCustomerModalOpen(false);
  };

  const handleScannedCode = async (code) => {
    if (!isValidBarcode(code)) {
      searchBarRef.current?.setQuery('');
      return;
    }

    // Cancelar cualquier consulta previa pendiente
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

  // Captura global de lecturas de pistolas de código de barras físicas (USB/Bluetooth)
  useScannerTrap(handleScannedCode);

  return (
    <div className="pos-page">
      {error && (
        <div className="alert alert-danger" style={{ marginBottom: '1rem' }}>
          {error}
        </div>
      )}

      {/* Indicador de Cliente Actual centrado y compatible con modo oscuro */}
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
            backgroundColor: '#0284c7',
            color: '#ffffff',
            border: 'none',
            borderRadius: '20px',
            padding: '4px 12px',
            boxShadow: '0 1px 3px rgba(0,0,0,0.15)',
            transition: 'all 0.2s ease',
            cursor: currentSale?.id ? 'pointer' : 'not-allowed',
            opacity: currentSale?.id ? 1 : 0.6,
            height: '26px'
          }}
          onClick={() => setIsCustomerModalOpen(true)}
          disabled={!currentSale?.id}
          title="Cambiar cliente asignado"
        >
          <Edit2 size={12} />
          Cambiar
        </button>
      </div>

      <div className="pos-layout">
        {/* Columna Izquierda: Búsqueda y Lista de Carrito */}
        <div className="pos-left-column">
          {/* Fila de búsqueda + escáner de cámara */}
          <div style={{ display: 'flex', gap: '0.6rem', alignItems: 'stretch', width: '100%' }}>
            <div style={{ flex: 1, minWidth: 0 }}>
              <SearchBar ref={searchBarRef} onSelectProduct={handleSelectProduct} />
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
                backgroundColor: 'var(--bg-surface)',
                border: '1px solid var(--border)',
                borderRadius: 'var(--radius-md)',
                boxShadow: 'var(--shadow-sm)',
                cursor: 'pointer',
                transition: 'all 0.15s ease',
              }}
            >
              <ScanLine size={20} />
            </button>
          </div>

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
                  onRemoveItem={removeItem}
                />
              </div>

              {/* Lista en Tarjetas para Móvil */}
              <div className="mobile-only">
                <CartList
                  items={items}
                  selectedItemId={selectedItemId}
                  onSelectItem={setSelectedItemId}
                  onUpdateQty={updateQuantity}
                  onRemoveItem={removeItem}
                />
              </div>
            </>
          )}
        </div>

        {/* Columna Derecha: Panel de Resumen y Cobro */}
        <div className="pos-right-column">
          <SummaryPanel onCheckout={onOpenCheckout} onHold={onOpenHold} />
        </div>
      </div>

      {/* Barra de Atajos Rápidos POS (Fija al pie en Desktop, Oculta en Móvil) */}
      <footer className="pos-shortcuts-bar">
        <div className="pos-shortcuts-title">
          <Keyboard size={15} className="color-primary" />
          <span>Atajos POS:</span>
        </div>
        <div className="pos-shortcuts-list">
          <span className="pos-shortcut-item"><kbd className="pos-shortcut-kbd">F1</kbd> Cobrar</span>
          <span className="pos-shortcut-item"><kbd className="pos-shortcut-kbd">F2</kbd> Buscar</span>
          <span className="pos-shortcut-item"><kbd className="pos-shortcut-kbd">F3</kbd> Cliente</span>
          <span className="pos-shortcut-item"><kbd className="pos-shortcut-kbd">F4</kbd> Espera</span>
          <span className="pos-shortcut-item"><kbd className="pos-shortcut-kbd">F5</kbd> Tasa BCV</span>
          <span className="pos-shortcut-item"><kbd className="pos-shortcut-kbd">F7</kbd> Detal/Mayor</span>
          <span className="pos-shortcut-item"><kbd className="pos-shortcut-kbd">F8</kbd> Cancelar Venta</span>
          <span className="pos-shortcut-item"><kbd className="pos-shortcut-kbd">+/-</kbd> Cantidad</span>
          <span className="pos-shortcut-item"><kbd className="pos-shortcut-kbd">Supr</kbd> Eliminar</span>
          <span className="pos-shortcut-item"><kbd className="pos-shortcut-kbd">Esc</kbd> Limpiar</span>
        </div>
      </footer>

      {/* Modal para Seleccionar Cliente */}
      <CustomerModal 
        isOpen={isCustomerModalOpen} 
        onClose={() => setIsCustomerModalOpen(false)} 
        onSelectCustomer={handleSelectCustomer}
        mode="select"
      />

      {/* Escáner de código de barras con cámara */}
      <BarcodeScannerModal
        isOpen={isScannerOpen}
        onClose={() => setIsScannerOpen(false)}
        onCodeScanned={handleScannedCode}
      />

      {/* Modal para Selección de Sabores / Variantes */}
      <VariantSelectorModal
        isOpen={!!variantParentProduct}
        parentProduct={variantParentProduct}
        onClose={() => setVariantParentProduct(null)}
        onSelectVariant={(variant) => addItem(variant, 1)}
        exchangeRate={exchangeRate}
      />
    </div>
  );
}
