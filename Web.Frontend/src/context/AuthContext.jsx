import { createContext, useContext, useState } from 'react';
import { api } from '../services/api';

const AuthContext = createContext(null);

// 8.5-WEB4: Whitelist estricta de campos del perfil de usuario. La forma dual "data.user || data"
// podía persistir campos no relacionados (tokens, flags internos) que lleguen dentro de "data".
// Solamente se conservan los campos consumidos por la UI; todo lo demás se descarta.
function normalizeUserProfile(src) {
  if (!src || typeof src !== 'object') return null;
  return {
    id: src.id ?? null,
    name: src.name ?? null,
    cedula: src.cedula ?? null,
    role: src.role ?? null,
  };
}

export function AuthProvider({ children }) {
  const [user, setUser] = useState(() => {
    try {
      const saved = localStorage.getItem('pos_user_profile');
      if (!saved) return null;
      const parsed = JSON.parse(saved);
      return normalizeUserProfile(parsed);
    } catch {
      return null;
    }
  });

  const login = async (cedula, password) => {
    if (!cedula || !cedula.trim()) {
      throw new Error('Por favor ingrese su número de cédula.');
    }
    if (!password) {
      throw new Error('Por favor ingrese su contraseña.');
    }

    const data = await api.post('/api/auth/login', {
      cedula: cedula.trim(),
      password: password,
      platform: 'Web'
    });

    if (data?.requiresPasswordChange) {
      return { requiresPasswordChange: true, message: data.message };
    }

    const sessionUser = normalizeUserProfile(data.user || data);

    if (!sessionUser) {
      throw new Error('La respuesta del servidor no contiene un perfil de usuario válido.');
    }

    setUser(sessionUser);
    localStorage.setItem('pos_user_profile', JSON.stringify(sessionUser));
    localStorage.removeItem('pos_token');
    return sessionUser;
  };

  const changePassword = async (cedula, currentPassword, newPassword) => {
    const res = await api.post('/api/auth/change-password', {
      cedula: cedula.trim(),
      currentPassword,
      newPassword
    });
    return res;
  };

  const logout = async () => {
    // 8.5-WEB3: El logout nunca debe ser silencioso en el servidor. Se intenta revocar la cookie
    // (una vez con timeout); si falla, se limpia el estado local y se advierte que el re-login puede
    // chocar hasta que el servidor revoque la sesión por expiración.
    let serverLogoutSucceeded = false;
    try {
      await api.post('/api/auth/logout', null, { signal: AbortSignal.timeout(5000) });
      serverLogoutSucceeded = true;
    } catch {
      serverLogoutSucceeded = false;
    }

    setUser(null);
    localStorage.removeItem('pos_user_profile');
    localStorage.removeItem('pos_user');
    localStorage.removeItem('pos_token');
    sessionStorage.clear();

    if (!serverLogoutSucceeded) {
      console.warn('[AuthContext] No se pudo confirmar la revocación de la sesión en el servidor.');
    }
  };

  return (
    <AuthContext.Provider value={{ user, login, changePassword, logout, isAuthenticated: !!user }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error('useAuth debe ser usado dentro de un AuthProvider');
  }
  return context;
}
