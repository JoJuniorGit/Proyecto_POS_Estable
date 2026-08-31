/**
 * Resuelve la URL base del Backend dinámicamente con la siguiente precedencia:
 * 1. Parámetro en URL (?api=... o ?server=...) al escanear QR -> Guarda en localStorage y limpia la URL.
 * 2. Valor guardado en localStorage ('pos_custom_api_url').
 * 3. Variable de entorno VITE_API_URL.
 * 4. Modo desarrollo Vite (puerto 5173 o dev): http://${hostname}:5000.
 * 5. Modo Kestrel integrado: window.location.origin.
 * 6. Fallback general: http://localhost:5000.
 */
function resolveBaseUrl() {
  if (typeof window === 'undefined') {
    return 'http://localhost:5000';
  }

  try {
    const urlParams = new URLSearchParams(window.location.search);
    const paramApi = urlParams.get('api') || urlParams.get('server') || urlParams.get('backend');
    if (paramApi) {
      let normalized = paramApi.trim();
      if (!normalized.startsWith('http://') && !normalized.startsWith('https://')) {
        normalized = `http://${normalized}`;
      }
      if (normalized.endsWith('/')) {
        normalized = normalized.slice(0, -1);
      }
      localStorage.setItem('pos_custom_api_url', normalized);

      // Limpiar los parámetros de la URL sin recargar la página
      urlParams.delete('api');
      urlParams.delete('server');
      urlParams.delete('backend');
      urlParams.delete('paired');
      const newQuery = urlParams.toString();
      const newUrl = window.location.pathname + (newQuery ? `?${newQuery}` : '') + window.location.hash;
      window.history.replaceState({}, document.title, newUrl);

      return normalized;
    }
  } catch {}

  const stored = localStorage.getItem('pos_custom_api_url');
  if (stored) {
    return stored;
  }

  if (import.meta.env.VITE_API_URL) {
    return import.meta.env.VITE_API_URL;
  }

  const hostname = window.location.hostname || 'localhost';
  const port = window.location.port;

  // Si estamos en Vite dev server (ej. 5173), el backend corre en el puerto 5000
  if (port === '5173' || (import.meta.env.DEV && port !== '5000')) {
    return `http://${hostname}:5000`;
  }

  // Si se sirve directamente desde el servidor Kestrel integrado
  if (window.location.origin && window.location.origin.startsWith('http')) {
    return window.location.origin;
  }

  return `http://${hostname}:5000`;
}

let CURRENT_BASE_URL = resolveBaseUrl();

export function setCustomBaseUrl(url) {
  if (typeof window === 'undefined') return;
  if (!url) {
    localStorage.removeItem('pos_custom_api_url');
    CURRENT_BASE_URL = resolveBaseUrl();
  } else {
    let normalized = url.trim();
    if (!normalized.startsWith('http://') && !normalized.startsWith('https://')) {
      normalized = `http://${normalized}`;
    }
    if (normalized.endsWith('/')) normalized = normalized.slice(0, -1);
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
