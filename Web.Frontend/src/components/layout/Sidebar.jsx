import {
  ShoppingCart,
  Package,
  History,
  Clock,
  PackageCheck,
  Landmark,
  ClipboardCheck,
  Settings,
  DollarSign,
  User,
  LogOut,
} from 'lucide-react';
import ThemeToggle from '../ui/ThemeToggle';
import { useAuth } from '../../context/AuthContext';

const NAV_SECTIONS = [
  {
    label: 'PRINCIPAL',
    items: [
      { id: 'pos', label: 'Punto de Venta', icon: ShoppingCart },
      { id: 'pending', label: 'Cuentas Abiertas', icon: Clock },
      { id: 'pickups', label: 'Retiros Pendientes', icon: PackageCheck },
    ],
  },
  {
    label: 'INVENTARIO',
    items: [
      { id: 'catalog', label: 'Catálogo', icon: Package },
      { id: 'history', label: 'Historial Ventas', icon: History },
      { id: 'register', label: 'Caja', icon: Landmark },
      { id: 'closing', label: 'Cierre Diario', icon: ClipboardCheck },
    ],
  },
  {
    label: 'SISTEMA',
    items: [
      { id: 'settings', label: 'Configuración', icon: Settings },
      { id: 'exchange', label: 'Tasa de Cambio', icon: DollarSign },
    ],
  },
];

export default function Sidebar({ currentView, onNavigate, isOpen, onClose }) {
  const { user, logout } = useAuth();

  const handleNav = (viewId) => {
    onNavigate(viewId);
    onClose();
  };

  const roleLabel = user?.role === 0 || user?.role === 'Admin' ? 'Administrador' : 'Cajero';

  return (
    <>
      {/* Overlay para móvil */}
      <div
        className={`sidebar-overlay ${isOpen ? 'visible' : ''}`}
        onClick={onClose}
      />

      <aside className={`sidebar ${isOpen ? 'open' : ''}`}>
        {/* Header */}
        <div className="sidebar-header">
          <div className="sidebar-brand">
            <ShoppingCart size={22} />
            <div>
              Administrador
              <div className="sidebar-brand-sub">Sistema POS</div>
            </div>
          </div>
        </div>

        {/* Navegación */}
        <nav className="sidebar-nav">
          {NAV_SECTIONS.map((section) => (
            <div key={section.label}>
              <div className="sidebar-section">{section.label}</div>
              {section.items.map((item) => (
                <button
                  key={item.id}
                  className={`sidebar-link ${currentView === item.id ? 'active' : ''}`}
                  onClick={() => handleNav(item.id)}
                >
                  <item.icon size={18} />
                  {item.label}
                </button>
              ))}
            </div>
          ))}
        </nav>

        {/* Footer */}
        <div className="sidebar-footer" style={{ flexDirection: 'column', gap: '0.75rem', alignItems: 'stretch' }}>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
            <ThemeToggle />
            <button
              type="button"
              onClick={logout}
              title="Cerrar Sesión"
              style={{
                background: 'transparent',
                border: 'none',
                color: '#ef4444',
                cursor: 'pointer',
                display: 'flex',
                alignItems: 'center',
                gap: '0.25rem',
                fontSize: '0.8rem',
                padding: '0.25rem 0.5rem',
                borderRadius: '0.25rem',
              }}
            >
              <LogOut size={16} /> Salir
            </button>
          </div>
          <div className="sidebar-user">
            <User size={14} />
            {user ? `${user.name} (${roleLabel})` : 'Sin sesión'}
          </div>
        </div>
      </aside>
    </>
  );
}
