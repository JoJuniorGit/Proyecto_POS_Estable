import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { useAuth } from './AuthContext';
import { getMonetaryProfile } from '../services/monetaryProfileApi';
import { getCurrencyFormat, setCachedMonetaryProfile } from '../utils/formatters';
import {
  DEFAULT_MONETARY_PROFILE,
  MONETARY_MODE_DUAL,
  formatWithSymbol,
  normalizeMonetaryProfile,
  symbolForCode,
} from '../utils/monetaryProfile';

export const MONETARY_PROFILE_STORAGE_KEY = 'monetary_profile_client';
export const MONETARY_PROFILE_UPDATED_EVENT = 'onMonetaryProfileUpdated';

const MonetaryProfileContext = createContext(null);

function getStorage() {
  try {
    if (typeof window !== 'undefined' && window.localStorage) return window.localStorage;
  } catch {
    return null;
  }
  return null;
}

/**
 * Lee la última copia normalizada del perfil. Un JSON corrupto o storage inaccesible devuelve
 * `null` para que el provider/preview use los defaults sin romper el arranque.
 * @returns {ReturnType<typeof normalizeMonetaryProfile> | null}
 */
export function readCachedMonetaryProfile() {
  const storage = getStorage();
  if (!storage) return null;

  try {
    const raw = storage.getItem(MONETARY_PROFILE_STORAGE_KEY);
    if (!raw) return null;
    return normalizeMonetaryProfile(JSON.parse(raw));
  } catch {
    return null;
  }
}

function writeCachedMonetaryProfile(profile) {
  const storage = getStorage();
  if (!storage) return;

  try {
    storage.setItem(MONETARY_PROFILE_STORAGE_KEY, JSON.stringify(normalizeMonetaryProfile(profile)));
  } catch {
    // El cache es una optimización: un storage lleno o bloqueado no debe romper la UI.
  }
}

export function MonetaryProfileProvider({ children }) {
  const { isAuthenticated } = useAuth();
  const [profile, setProfile] = useState(() => {
    const initial = readCachedMonetaryProfile() ?? DEFAULT_MONETARY_PROFILE;
    setCachedMonetaryProfile(initial);
    return initial;
  });
  const [isLoaded, setIsLoaded] = useState(false);
  const isMountedRef = useRef(true);
  const abortRef = useRef(null);

  const applyProfile = useCallback((nextProfile) => {
    const normalized = normalizeMonetaryProfile(nextProfile);
    setCachedMonetaryProfile(normalized);
    setProfile(normalized);
    writeCachedMonetaryProfile(normalized);
    return normalized;
  }, []);

  // Refresco autoritativo: una lectura nueva reemplaza el cache y el estado. La petición anterior
  // se aborta para que un focus rápido no aplique una respuesta vieja sobre una más reciente.
  const refreshProfile = useCallback(async () => {
    abortRef.current?.abort();
    const controller = typeof AbortController === 'function' ? new AbortController() : null;
    abortRef.current = controller;

    try {
      const raw = await getMonetaryProfile(controller?.signal);
      if (!isMountedRef.current) return null;
      return applyProfile(raw);
    } catch (error) {
      if (error?.name !== 'AbortError') {
        console.warn(
          '[MonetaryProfileContext] No se pudo cargar el perfil monetario; se usa la caché/default.',
          error?.message || error
        );
      }
      return null;
    } finally {
      if (isMountedRef.current) setIsLoaded(true);
    }
  }, [applyProfile]);

  useEffect(() => {
    isMountedRef.current = true;
    return () => {
      isMountedRef.current = false;
      abortRef.current?.abort();
    };
  }, []);

  // El endpoint exige autenticación: no se consulta en la pantalla de login (un 401 dispararía
  // pos_unauthorized y limpiaría la sesión en memoria). Al autenticarse se carga una sola vez.
  useEffect(() => {
    if (!isAuthenticated || typeof window === 'undefined') return undefined;

    void refreshProfile();

    const handleProfileUpdated = (event) => {
      const detail = event?.detail;
      if (detail && typeof detail === 'object' && !Array.isArray(detail)) {
        applyProfile(detail);
        setIsLoaded(true);
        return;
      }
      void refreshProfile();
    };

    const handleStorage = (event) => {
      if (event?.key && event.key !== MONETARY_PROFILE_STORAGE_KEY) return;
      const cached = readCachedMonetaryProfile();
      if (!cached) return;
      setProfile(cached);
      setIsLoaded(true);
    };

    const handleFocus = () => {
      void refreshProfile();
    };

    window.addEventListener(MONETARY_PROFILE_UPDATED_EVENT, handleProfileUpdated);
    window.addEventListener('storage', handleStorage);
    window.addEventListener('focus', handleFocus);

    return () => {
      window.removeEventListener(MONETARY_PROFILE_UPDATED_EVENT, handleProfileUpdated);
      window.removeEventListener('storage', handleStorage);
      window.removeEventListener('focus', handleFocus);
      abortRef.current?.abort();
    };
  }, [isAuthenticated, applyProfile, refreshProfile]);

  const value = useMemo(() => {
    const formatPrimary = (amount, decimals = 2) => formatWithSymbol(
      profile.primaryCurrencySymbol,
      amount,
      getCurrencyFormat(),
      decimals
    );

    // Single no tiene una segunda moneda: el llamador recibe la presentación primaria.
    const formatSecondary = profile.mode === MONETARY_MODE_DUAL
      ? (amount, decimals = 2) => formatWithSymbol(
        profile.secondaryCurrencySymbol,
        amount,
        getCurrencyFormat(),
        decimals
      )
      : formatPrimary;

    return {
      profile,
      mode: profile.mode,
      primaryCurrencyCode: profile.primaryCurrencyCode,
      primaryCurrencySymbol: profile.primaryCurrencySymbol,
      secondaryCurrencyCode: profile.secondaryCurrencyCode,
      secondaryCurrencySymbol: profile.secondaryCurrencySymbol,
      isConfigured: profile.isConfigured,
      isLoaded,
      refreshProfile,
      symbolFor: (code) => symbolForCode(profile, code),
      formatPrimary,
      formatSecondary,
    };
  }, [profile, isLoaded, refreshProfile]);

  return (
    <MonetaryProfileContext.Provider value={value}>
      {children}
    </MonetaryProfileContext.Provider>
  );
}

/**
 * Hook de consumo con fallback seguro fuera del provider (tests, previews o montajes aislados).
 * El fallback usa el cache local si existe y, si no, el perfil default Dual USD/VES.
 */
export function useMonetaryProfile() {
  const context = useContext(MonetaryProfileContext);
  if (context) return context;

  const profile = readCachedMonetaryProfile() ?? DEFAULT_MONETARY_PROFILE;
  const formatPrimary = (amount, decimals = 2) => formatWithSymbol(
    profile.primaryCurrencySymbol,
    amount,
    getCurrencyFormat(),
    decimals
  );
  const formatSecondary = profile.mode === MONETARY_MODE_DUAL
    ? (amount, decimals = 2) => formatWithSymbol(
      profile.secondaryCurrencySymbol,
      amount,
      getCurrencyFormat(),
      decimals
    )
    : formatPrimary;

  return {
    profile,
    mode: profile.mode,
    primaryCurrencyCode: profile.primaryCurrencyCode,
    primaryCurrencySymbol: profile.primaryCurrencySymbol,
    secondaryCurrencyCode: profile.secondaryCurrencyCode,
    secondaryCurrencySymbol: profile.secondaryCurrencySymbol,
    isConfigured: profile.isConfigured,
    isLoaded: true,
    refreshProfile: () => Promise.resolve(null),
    symbolFor: (code) => symbolForCode(profile, code),
    formatPrimary,
    formatSecondary,
  };
}
