import { createContext, useContext, useState, useEffect, useCallback } from 'react';
import {
  THEME_STORAGE_KEY,
  THEME_SYNC_CHANNEL,
  THEME_VALUES,
  readStoredState,
  writeStoredState,
  broadcastStateSync,
  subscribeStateSync,
  subscribeWindowRevalidation,
  resolveRevalidatedValue,
} from '../utils/crossTabStateSync';

const ThemeContext = createContext();

export function ThemeProvider({ children }) {
  const [theme, setTheme] = useState(() => readStoredState(THEME_STORAGE_KEY, THEME_VALUES) || 'dark');

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme);
    writeStoredState(THEME_STORAGE_KEY, theme);
    broadcastStateSync(THEME_SYNC_CHANNEL, theme);
  }, [theme]);

  useEffect(() => subscribeStateSync(THEME_SYNC_CHANNEL, THEME_VALUES, (value) => {
    setTheme((prev) => (prev === value ? prev : value));
  }), []);

  useEffect(() => subscribeWindowRevalidation(() => {
    const stored = readStoredState(THEME_STORAGE_KEY, THEME_VALUES);
    if (stored) setTheme((prev) => resolveRevalidatedValue(stored, prev, THEME_VALUES));
  }), []);

  const toggleTheme = useCallback(() => {
    setTheme(prev => prev === 'light' ? 'dark' : 'light');
  }, []);

  return (
    <ThemeContext.Provider value={{ theme, toggleTheme }}>
      {children}
    </ThemeContext.Provider>
  );
}

export function useTheme() {
  const context = useContext(ThemeContext);
  if (!context) throw new Error('useTheme must be used within a ThemeProvider');
  return context;
}
