import { useState } from 'react';
import { useCart } from '../context/CartContext';
import SearchBar from '../components/pos/SearchBar';
import EmptyCart from '../components/pos/EmptyCart';
import CartTable from '../components/pos/CartTable';
import CartList from '../components/pos/CartList';
import SummaryPanel from '../components/pos/SummaryPanel';
import CustomerModal from '../components/pos/CustomerModal';
import { Edit2 } from 'lucide-react';

export default function PosPage({ onOpenCheckout, onOpenHold }) {
  const [isCustomerModalOpen, setIsCustomerModalOpen] = useState(false);
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
    error,
  } = useCart();

  const handleSelectProduct = (product) => {
    addItem(product, 1);
  };

  const handleSelectCustomer = async (customerId) => {
    await updateCustomer(customerId);
    setIsCustomerModalOpen(false);
  };

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
          <SearchBar onSelectProduct={handleSelectProduct} />

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

      {/* Modal para Seleccionar Cliente */}
      <CustomerModal 
        isOpen={isCustomerModalOpen} 
        onClose={() => setIsCustomerModalOpen(false)} 
        onSelectCustomer={handleSelectCustomer}
        mode="select"
      />
    </div>
  );
}
