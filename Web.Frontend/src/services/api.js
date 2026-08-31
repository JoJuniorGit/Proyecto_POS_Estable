/**
 * Resuelve la URL base del Backend dinámicamente con la siguiente precedencia:
 * 1. Modo Kestrel integrado en puerto 5000 o 5001 -> window.location.origin.
 * 2. Parámetro en URL (?api=... o ?server=...) al escanear QR -> Sanitiza y guarda en localStorage.
 * 3. Valor guardado en localStorage ('pos_custom_api_url') -> Sanitiza si la página está en HTTPS para evitar Mixed Content.
 * 4. Variable de entorno VITE_API_URL (si está configurada).
 * 5. Modo desarrollo Vite (puerto 5173 u otros) -> http://${hostname}:5000 o https://${hostname}:5001 según protocolo.
 * 6. Fallback general: window.location.origin o http://${hostname}:5000.
 */
export function resolveBaseUrl() {
  if (typeof window === 'undefined') {
    return 'http://localhost:5000';
  }

  const isHttps = window.location?.protocol === 'https:';
  const origin = window.location?.origin;
  const hostname = window.location?.hostname || 'localhost';
  const port = window.location?.port ? String(window.location.port) : '';

  // 1. Si se sirve directamente desde el servidor Kestrel integrado en 5000 o 5001
  if (port === '5000' || port === '5001') {
    return origin || (isHttps ? `https://${hostname}:5001` : `http://${hostname}:5000`);
  }

  // 2. Parámetro en URL (?api=... o ?server=...)
  try {
    const search = window.location?.search || '';
    const urlParams = new URLSearchParams(search);
    const paramApi = urlParams.get('api') || urlParams.get('server') || urlParams.get('backend');
    if (paramApi) {
      let normalized = paramApi.trim();
      if (!normalized.startsWith('http://') && !normalized.startsWith('https://')) {
        normalized = isHttps ? `https://${normalized}` : `http://${normalized}`;
      }
      if (normalized.endsWith('/')) {
        normalized = normalized.slice(0, -1);
      }
      // Sanitización: si la página es HTTPS pero el param trajo http:// o :5000, actualizar protocolo y mapear :5000 -> :5001
      if (isHttps) {
        if (normalized.startsWith('http://')) {
          normalized = normalized.replace(/^http:\/\//i, 'https://');
        }
        if (normalized.endsWith(':5000')) {
          normalized = normalized.replace(/:5000$/, ':5001');
        }
      }

      try {
        localStorage.setItem('pos_custom_api_url', normalized);
      } catch {}

      // Limpiar los parámetros de la URL sin recargar la página
      if (window.history?.replaceState && window.location?.pathname) {
        urlParams.delete('api');
        urlParams.delete('server');
        urlParams.delete('backend');
        urlParams.delete('paired');
        const newQuery = urlParams.toString();
        const newUrl = window.location.pathname + (newQuery ? `?${newQuery}` : '') + (window.location.hash || '');
        window.history.replaceState({}, (typeof document !== 'undefined' ? document.title : ''), newUrl);
      }

      return normalized;
    }
  } catch {}

  // 3. Sanitización de valor almacenado en localStorage
  try {
    let stored = localStorage.getItem('pos_custom_api_url');
    if (stored) {
      let sanitized = stored.trim();
      if (isHttps && sanitized.startsWith('http://')) {
        sanitized = sanitized.replace(/^http:\/\//i, 'https://').replace(/:5000$/, ':5001');
        localStorage.setItem('pos_custom_api_url', sanitized);
      }
      return sanitized;
    }
  } catch {}

  // 4. Variables de entorno (con guard seguro para Node/Jest/Vitest/Vite)
  const isDev = typeof import.meta !== 'undefined' && import.meta.env?.DEV;
  const viteApiUrl = typeof import.meta !== 'undefined' ? import.meta.env?.VITE_API_URL : undefined;
  if (viteApiUrl) {
    return viteApiUrl;
  }

  // 5. Servidor de desarrollo Vite (ej. puerto 5173 o puerto no Kestrel)
  if (port === '5173' || (isDev && port !== '5000' && port !== '5001')) {
    return isHttps ? `https://${hostname}:5001` : `http://${hostname}:5000`;
  }

  // 6. Si hay origin válido en Kestrel u otro servidor
  if (origin && origin.startsWith('http')) {
    return origin;
  }

  return isHttps ? `https://${hostname}:5001` : `http://${hostname}:5000`;
}

let CURRENT_BASE_URL = resolveBaseUrl();

export function setCustomBaseUrl(url) {
  if (typeof window === 'undefined') return;
  const isHttps = window.location?.protocol === 'https:';

  if (!url) {
    localStorage.removeItem('pos_custom_api_url');
    CURRENT_BASE_URL = resolveBaseUrl();
  } else {
    let normalized = url.trim();
    if (!normalized.startsWith('http://') && !normalized.startsWith('https://')) {
      normalized = isHttps ? `https://${normalized}` : `http://${normalized}`;
    }
    if (normalized.endsWith('/')) normalized = normalized.slice(0, -1);

    if (isHttps) {
      if (normalized.startsWith('http://')) {
        normalized = normalized.replace(/^http:\/\//i, 'https://');
      }
      if (normalized.endsWith(':5000')) {
        normalized = normalized.replace(/:5000$/, ':5001');
      }
    }

    localStorage.setItem('pos_custom_api_url', normalized);
    CURRENT_BASE_URL = normalized;
  }
}


/**
 * Realiza una petición HTTP al backend.
 * @param {string} endpoint - Ruta relativa (ej: "/api/products/suggestions")
 * @param {object} options - Opciones adicionales de fetch (method, body, headers, signal, etc.)
 * @returns {Promise<any>} - La respuesta parseada como JSON, o null para 204 No Content.
 */
export async function apiFetch(endpoint, options = {}) {
  const url = `${CURRENT_BASE_URL}${endpoint}`;

  const userStr = localStorage.getItem('pos_user');
  let userHeaders = {};
  if (userStr) {
    try {
      const u = JSON.parse(userStr);
      const token = u?.token || u?.Token || localStorage.getItem('pos_token');
      if (token) {
        userHeaders['Authorization'] = `Bearer ${token}`;
      }
      if (u?.id) userHeaders['X-User-Id'] = String(u.id);
      if (u?.role !== undefined) userHeaders['X-User-Role'] = String(u.role);
    } catch {}
  }

  const config = {
    headers: {
      'Content-Type': 'application/json',
      'Accept': 'application/json',
      'X-Client-Version': '1.0.0',
      ...userHeaders,
      ...options.headers,
    },
    ...options,
  };

  // No enviar Content-Type para peticiones sin body (GET, DELETE)
  if (!config.body) {
    delete config.headers['Content-Type'];
  }

  const response = await fetch(url, config);

  // 204 No Content — no hay body que parsear
  if (response.status === 204) {
    return null;
  }

  if (!response.ok) {
    if (response.status === 401 && !endpoint.includes('api/auth/login')) {
      try {
        localStorage.removeItem('pos_user');
        localStorage.removeItem('pos_token');
        if (typeof window !== 'undefined') {
          window.dispatchEvent(new CustomEvent('pos_unauthorized'));
        }
      } catch {}
    }

    let errorMessage = response.status === 401
      ? 'Cédula o contraseña incorrecta.'
      : `Error ${response.status}: ${response.statusText}`;

    try {
      const errorBody = await response.text();
      if (errorBody) {
        try {
          const jsonErr = JSON.parse(errorBody);
          if (jsonErr.message) errorMessage = jsonErr.message;
          else if (jsonErr.Message) errorMessage = jsonErr.Message;
          else if (jsonErr.requiresPasswordChange) {
            const err = new Error(jsonErr.message || 'Debe cambiar su contraseña antes de continuar.');
            err.requiresPasswordChange = true;
            throw err;
          }
        } catch (e) {
          if (e.requiresPasswordChange) throw e;
          if (!errorBody.includes('<html') && errorBody.length < 300) {
            errorMessage = errorBody;
          }
        }
      }
    } catch (e) {
      if (e.requiresPasswordChange) throw e;
    }
    throw new Error(errorMessage);
  }

  // Intentar parsear como JSON, si falla retornar texto plano
  const contentType = response.headers.get('content-type');
  if (contentType && contentType.includes('application/json')) {
    return response.json();
  }

  return response.text();
}

/**
 * Atajos para métodos HTTP comunes.
 */
export const api = {
  get: (endpoint, signal) =>
    apiFetch(endpoint, { method: 'GET', signal }),

  post: (endpoint, body, signal) =>
    apiFetch(endpoint, {
      method: 'POST',
      body: body ? JSON.stringify(body) : undefined,
      signal,
    }),

  put: (endpoint, body, signal) =>
    apiFetch(endpoint, {
      method: 'PUT',
      body: body ? JSON.stringify(body) : undefined,
      signal,
    }),

  delete: (endpoint, signal) =>
    apiFetch(endpoint, { method: 'DELETE', signal }),
};

/**
 * Expone la URL base para que el servicio de SignalR pueda construir
 * la URL del hub sin duplicar la configuración.
 */
export function getBaseUrl() {
  return CURRENT_BASE_URL;
}
