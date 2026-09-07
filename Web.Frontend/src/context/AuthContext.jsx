import { createContext, useContext, useEffect, useState } from 'react';
import { api } from '../services/api';

const AuthContext = createContext(null);

// 8.9-L12: el perfil local almacenado no debe sobrevivir indefinidamente ni quedar válido en
// caché hasta el próximo 401. Se marca con un sello de expiración (24 h, alineado con la vida
// típica de la sesión HTTP) y se descarta al vencerse, forzando re-login.
const PROFILE_CACHE_TTL_MS = 24 * 60 * 60 * 1000;
const PROFILE_TS_KEY = 'pos_user_profile_ts';

function isProfileExpired() {
  try {
    const ts = Number(localStorage.getItem(PROFILE_TS_KEY) || 0);
    return !ts || Date.now() - ts > PROFILE_CACHE_TTL_MS;
  } catch {
    return true;
  }
}

function clearStoredProfile() {
  try {
    localStorage.removeItem('pos_user_profile_ts');
  } catch {}
}

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
      // 8.9-L12: perfil caducado => fuera de sesión (no esperar al próximo 401).
      if (isProfileExpired()) {
        localStorage.removeItem('pos_user_profile');
        clearStoredProfile();
        return null;
      }
      const parsed = JSON.parse(saved);
      return normalizeUserProfile(parsed);
    } catch {
      return null;
    }
  });

  // 8.7-M1: la UI debe reaccionar al 401/revocación. api.js emite 'pos_unauthorized' cuando el
  // servidor rechaza el token (stamp inválido tras logout/rotación o expiración). Aquí se limpia
  // el estado de sesión en memoria para volver a la pantalla de login.
  useEffect(() => {
    if (typeof window === 'undefined') return undefined;

    const handleUnauthorized = () => {
      setUser(null);
      localStorage.removeItem('pos_user_profile');
      localStorage.removeItem('pos_user');
      localStorage.removeItem('pos_token');
      clearStoredProfile();
      sessionStorage.clear();
    };

    window.addEventListener('pos_unauthorized', handleUnauthorized);
    return () => window.removeEventListener('pos_unauthorized', handleUnauthorized);
  }, []);

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
    try {
      localStorage.setItem(PROFILE_TS_KEY, String(Date.now()));
    } catch {}
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
    clearStoredProfile();
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
