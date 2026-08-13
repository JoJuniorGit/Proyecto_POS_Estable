import { ShoppingCart } from 'lucide-react';

export default function EmptyCart() {
  return (
    <div className="empty-cart-container">
      <div className="empty-cart-icon-wrapper">
        <ShoppingCart size={48} className="empty-cart-icon" />
      </div>
      <h3 className="empty-cart-title">El carrito está vacío</h3>
      <p className="empty-cart-text">
        Busca un producto en la barra superior para comenzar a agregar ítems a la venta.
      </p>
    </div>
  );
}
