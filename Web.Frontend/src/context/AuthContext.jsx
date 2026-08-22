import { createContext, useContext, useState } from 'react';
import { api } from '../services/api';

const AuthContext = createContext(null);

export function AuthProvider({ children }) {
  const [user, setUser] = useState(() => {
    try {
      const saved = localStorage.getItem('pos_user');
      return saved ? JSON.parse(saved) : null;
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
      password: password
    });

    if (data?.requiresPasswordChange) {
      return { requiresPasswordChange: true, message: data.message };
    }

    const sessionUser = {
      ...(data.user || data),
      token: data.token
    };

    setUser(sessionUser);
    localStorage.setItem('pos_user', JSON.stringify(sessionUser));
    if (data.token) {
      localStorage.setItem('pos_token', data.token);
    }
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

  const logout = () => {
    setUser(null);
    localStorage.removeItem('pos_user');
    localStorage.removeItem('pos_token');
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
