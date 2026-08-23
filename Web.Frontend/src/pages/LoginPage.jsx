import { useState } from 'react';
import { useAuth } from '../context/AuthContext';
import { UserCheck, KeyRound, Lock, Eye, EyeOff, Loader2, LogIn, CheckCircle2 } from 'lucide-react';
import './LoginPage.css';

export default function LoginPage() {
  const { login, changePassword } = useAuth();
  const [cedula, setCedula] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  // Estado para cambio de contraseña obligatorio
  const [mustChange, setMustChange] = useState(false);
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [showNewPassword, setShowNewPassword] = useState(false);

  const handleSubmit = async (e) => {
    e.preventDefault();
    if (!cedula.trim()) {
      setError('Por favor ingrese su usuario.');
      return;
    }
    if (!password) {
      setError('Por favor ingrese su contraseña.');
      return;
    }

    setLoading(true);
    setError('');

    try {
      const result = await login(cedula, password);
      if (result?.requiresPasswordChange) {
        setMustChange(true);
      }
    } catch (err) {
      if (err.requiresPasswordChange) {
        setMustChange(true);
      } else {
        setError(err.message || 'Usuario o contraseña incorrectos.');
      }
    } finally {
      setLoading(false);
    }
  };

  const handleChangePasswordSubmit = async (e) => {
    e.preventDefault();
    if (!newPassword || newPassword.length < 6) {
      setError('La nueva contraseña debe tener al menos 6 caracteres.');
      return;
    }
    if (newPassword !== confirmPassword) {
      setError('Las contraseñas no coinciden.');
      return;
    }

    setLoading(true);
    setError('');

    try {
      await changePassword(cedula, password, newPassword);
      // Auto-login con la nueva contraseña
      await login(cedula, newPassword);
    } catch (err) {
      setError(err.message || 'Error al actualizar la contraseña.');
    } finally {
      setLoading(false);
    }
  };

  if (mustChange) {
    return (
      <div className="login-container">
        <div className="login-card">
          <div className="login-header">
            <div className="login-icon password-change-icon">
              <KeyRound size={32} />
            </div>
            <h1 className="login-title">Actualizar Contraseña</h1>
            <p className="login-subtitle">
              Es necesario establecer una nueva contraseña permanente para continuar
            </p>
          </div>

          <form onSubmit={handleChangePasswordSubmit} className="login-form">
            {error && <div className="login-error">{error}</div>}

            <div className="login-input-group">
              <div className="login-input-icon">
                <Lock size={18} />
              </div>
              <input
                type={showNewPassword ? 'text' : 'password'}
                className="login-input with-icon"
                placeholder="Nueva Contraseña"
                value={newPassword}
                onChange={(e) => setNewPassword(e.target.value)}
                disabled={loading}
                autoFocus
              />
              <button
                type="button"
                className="login-eye-btn"
                onClick={() => setShowNewPassword(!showNewPassword)}
                tabIndex={-1}
              >
                {showNewPassword ? <EyeOff size={18} /> : <Eye size={18} />}
              </button>
            </div>

            <div className="login-input-group">
              <div className="login-input-icon">
                <Lock size={18} />
              </div>
              <input
                type={showNewPassword ? 'text' : 'password'}
                className="login-input with-icon"
                placeholder="Confirmar Contraseña"
                value={confirmPassword}
                onChange={(e) => setConfirmPassword(e.target.value)}
                disabled={loading}
              />
            </div>

            <button type="submit" className="login-btn" disabled={loading}>
              {loading ? (
                <>
                  <Loader2 className="animate-spin" size={20} /> Guardando...
                </>
              ) : (
                <>
                  <CheckCircle2 size={20} /> GUARDAR Y ENTRAR
                </>
              )}
            </button>

            <button
              type="button"
              className="login-cancel-btn"
              onClick={() => {
                setMustChange(false);
                setPassword('');
                setError('');
              }}
              disabled={loading}
            >
              Cancelar
            </button>
          </form>
        </div>
      </div>
    );
  }

  return (
    <div className="login-container">
      <div className="login-card">
        <div className="login-header">
          <div className="login-icon">
            <UserCheck size={32} />
          </div>
          <h1 className="login-title">Inicio de Sesión POS</h1>
          <p className="login-subtitle">Ingrese sus credenciales de acceso para continuar</p>
        </div>

        <form onSubmit={handleSubmit} className="login-form">
          {error && <div className="login-error">{error}</div>}

          <div className="login-input-group">
            <div className="login-input-icon">
              <UserCheck size={18} />
            </div>
            <input
              type="text"
              className="login-input with-icon"
              placeholder="Usuario (ej: admin o cajero)"
              value={cedula}
              onChange={(e) => setCedula(e.target.value)}
              disabled={loading}
              autoFocus
            />
          </div>

          <div className="login-input-group">
            <div className="login-input-icon">
              <Lock size={18} />
            </div>
            <input
              type={showPassword ? 'text' : 'password'}
              className="login-input with-icon"
              placeholder="Contraseña"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              disabled={loading}
            />
            <button
              type="button"
              className="login-eye-btn"
              onClick={() => setShowPassword(!showPassword)}
              tabIndex={-1}
            >
              {showPassword ? <EyeOff size={18} /> : <Eye size={18} />}
            </button>
          </div>

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
