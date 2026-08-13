import { useState } from 'react';
import Sidebar from './Sidebar';
import TopBar from './TopBar';

const VIEW_TITLES = {
  pos: 'Punto de Venta',
  catalog: 'Catálogo de Productos',
  history: 'Historial de Ventas',
  register: 'Caja Registradora',
  closing: 'Cierre Diario',
  settings: 'Configuración',
  exchange: 'Tasa de Cambio',
};

export default function Layout({ children, currentView, onNavigate, exchangeRate }) {
  const [sidebarOpen, setSidebarOpen] = useState(false);

  return (
    <div className="app-layout">
      <Sidebar
        currentView={currentView}
        onNavigate={onNavigate}
        isOpen={sidebarOpen}
        onClose={() => setSidebarOpen(false)}
      />

      <div className="app-main">
        <TopBar
          title={VIEW_TITLES[currentView] || 'POS'}
          exchangeRate={exchangeRate}
          onMenuClick={() => setSidebarOpen(true)}
        />

        <main className="app-content">
          {children}
        </main>
      </div>
    </div>
  );
}
