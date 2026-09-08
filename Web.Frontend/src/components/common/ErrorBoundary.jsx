import React from 'react';
import { AlertTriangle, RefreshCw, RotateCcw } from 'lucide-react';

export default class ErrorBoundary extends React.Component {
  constructor(props) {
    super(props);
    this.state = { hasError: false, error: null };
  }

  static getDerivedStateFromError(error) {
    return { hasError: true, error };
  }

  componentDidCatch(error, errorInfo) {
    console.error('[ErrorBoundary] Error no controlado capturado:', error, errorInfo);
  }

  handleReload = () => {
    window.location.reload();
  };

  handleReset = () => {
    try {
      sessionStorage.clear();
      localStorage.removeItem('pos_active_view');
      localStorage.removeItem('active_pos_sale_id');
      localStorage.removeItem('active_pos_sale_cache');
      localStorage.removeItem('active_pos_has_items');
    } catch {}
    window.location.hash = 'pos';
    window.location.reload();
  };

  render() {
    if (this.state.hasError) {
      const errorMsg = this.state.error?.message || 'Error desconocido';

      return (
        <div className="error-boundary-shell">
          <div className="error-boundary-card">
            <div className="error-boundary-icon">
              <AlertTriangle size={32} />
            </div>

            <h1 className="error-boundary-title">
              Error al Cargar la Interfaz
            </h1>

            <p className="error-boundary-message">
              Se ha detectado una excepción inesperada durante el renderizado. Puede intentar recargar la vista o reiniciar la sesión de trabajo.
            </p>

            <div className="error-boundary-details">
              {errorMsg}
            </div>

            <div className="error-boundary-actions">
              <button
                type="button"
                onClick={this.handleReload}
                className="error-boundary-btn error-boundary-btn-primary"
              >
                <RefreshCw size={18} /> Recargar Vista
              </button>

              <button
                type="button"
                onClick={this.handleReset}
                className="error-boundary-btn error-boundary-btn-secondary"
              >
                <RotateCcw size={18} /> Reiniciar Estado de Sesión
              </button>
            </div>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}
