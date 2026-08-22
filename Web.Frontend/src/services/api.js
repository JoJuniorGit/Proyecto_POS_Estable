/**
 * Servicio API base — Wrapper sobre fetch nativo.
 *
 * La URL base se lee de la variable de entorno VITE_API_URL definida en .env.
 * Esto permite que dispositivos móviles en la red local se conecten al backend
 * usando el hostname o IP de la PC servidor.
 *
 * Ejemplo .env:
 *   VITE_API_URL=http://laptop:5000
 *   VITE_API_URL=http://192.168.1.15:5000
 */

const hostname = typeof window !== 'undefined' ? window.location.hostname : 'localhost';
const defaultBaseUrl = `http://${hostname}:5000`;

const BASE_URL = import.meta.env.VITE_API_URL || defaultBaseUrl;


/**
 * Realiza una petición HTTP al backend.
 * @param {string} endpoint - Ruta relativa (ej: "/api/products/suggestions")
 * @param {object} options - Opciones adicionales de fetch (method, body, headers, signal, etc.)
 * @returns {Promise<any>} - La respuesta parseada como JSON, o null para 204 No Content.
 */
export async function apiFetch(endpoint, options = {}) {
  const url = `${BASE_URL}${endpoint}`;

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
  return BASE_URL;
}
