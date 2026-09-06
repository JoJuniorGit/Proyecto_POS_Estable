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
        <div
          style={{
            minHeight: '100vh',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            backgroundColor: 'var(--bg-primary, #0F172A)',
            color: 'var(--text-primary, #F1F5F9)',
            padding: '1.5rem',
            fontFamily: 'Inter, system-ui, sans-serif',
          }}
        >
          <div
            style={{
              maxWidth: '480px',
              width: '100%',
              backgroundColor: 'var(--bg-surface, #1E293B)',
              border: '1px solid var(--border, #334155)',
              borderRadius: '16px',
              padding: '2.5rem 2rem',
              textAlign: 'center',
              boxShadow: '0 20px 25px -5px rgba(0, 0, 0, 0.4)',
            }}
          >
            <div
              style={{
                width: '64px',
                height: '64px',
                borderRadius: '50%',
                backgroundColor: 'rgba(239, 68, 68, 0.15)',
                color: '#EF4444',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                margin: '0 auto 1.5rem',
              }}
            >
              <AlertTriangle size={32} />
            </div>

            <h1
              style={{
                fontSize: '1.35rem',
                fontWeight: 700,
                marginBottom: '0.75rem',
                color: 'var(--text-primary, #F1F5F9)',
              }}
            >
              Error al Cargar la Interfaz
            </h1>

            <p
              style={{
                fontSize: '0.9rem',
                color: 'var(--text-muted, #94A3B8)',
                lineHeight: 1.5,
                marginBottom: '1.25rem',
              }}
            >
              Se ha detectado una excepción inesperada durante el renderizado. Puede intentar recargar la vista o reiniciar la sesión de trabajo.
            </p>

            <div
              style={{
                backgroundColor: 'rgba(0, 0, 0, 0.25)',
                border: '1px solid var(--border, #334155)',
                borderRadius: '8px',
                padding: '0.75rem 1rem',
                fontSize: '0.8rem',
                fontFamily: 'monospace',
                color: '#F87171',
                textAlign: 'left',
                overflowX: 'auto',
                marginBottom: '1.75rem',
                maxHeight: '120px',
              }}
            >
              {errorMsg}
            </div>

            <div style={{ display: 'flex', gap: '0.75rem', flexDirection: 'column' }}>
              <button
                type="button"
                onClick={this.handleReload}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  gap: '0.5rem',
                  padding: '0.75rem 1.25rem',
                  fontSize: '0.95rem',
                  fontWeight: 600,
                  color: '#FFFFFF',
                  backgroundColor: 'var(--accent-primary, #673AB7)',
                  border: 'none',
                  borderRadius: '10px',
                  cursor: 'pointer',
                  transition: 'opacity 0.2s ease',
                }}
              >
                <RefreshCw size={18} /> Recargar Vista
              </button>

              <button
                type="button"
                onClick={this.handleReset}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  gap: '0.5rem',
                  padding: '0.75rem 1.25rem',
                  fontSize: '0.95rem',
                  fontWeight: 600,
                  color: 'var(--text-muted, #94A3B8)',
                  backgroundColor: 'transparent',
                  border: '1px solid var(--border, #334155)',
                  borderRadius: '10px',
                  cursor: 'pointer',
                }}
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
