import { useState } from 'react';
import { useAuth } from '../context/AuthContext';
import { UserCheck, Loader2, LogIn } from 'lucide-react';
import './LoginPage.css';

export default function LoginPage() {
  const { login } = useAuth();
  const [cedula, setCedula] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const handleSubmit = async (e) => {
    e.preventDefault();
    if (!cedula.trim()) {
      setError('Por favor ingrese su número de cédula.');
      return;
    }

    setLoading(true);
    setError('');

    try {
      await login(cedula);
    } catch (err) {
      setError(err.message || 'Cédula no válida o usuario inactivo.');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="login-container">
      <div className="login-card">
        <div className="login-header">
          <div className="login-icon">
            <UserCheck size={32} />
          </div>
          <h1 className="login-title">Inicio de Sesión POS</h1>
          <p className="login-subtitle">Ingrese su Cédula de Identidad para continuar</p>
        </div>

        <form onSubmit={handleSubmit} className="login-form">
          {error && <div className="login-error">{error}</div>}

          <input
            type="text"
            className="login-input"
            placeholder="Cédula de Identidad"
            value={cedula}
            onChange={(e) => setCedula(e.target.value)}
            disabled={loading}
            autoFocus
          />

          <button type="submit" className="login-btn" disabled={loading}>
            {loading ? (
              <>
                <Loader2 className="animate-spin" size={20} /> Validando...
              </>
            ) : (
              <>
                <LogIn size={20} /> INGRESAR
              </>
            )}
          </button>
        </form>
      </div>
    </div>
  );
}
