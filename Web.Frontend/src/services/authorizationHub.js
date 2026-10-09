import * as signalR from '@microsoft/signalr';
import { ApiError, api, getBaseUrl } from './api';

// 8.150 (T7, design D7): segundo hub del cliente web para autorizaciones remotas.
// Cookie auth, auto-reconnect e invoke + fallback REST (mismo par que expone el backend).

export const AUTHORIZATION_HUB_PATH = '/hubs/authorization';

export const AUTHORIZATION_EVENT_NAMES = Object.freeze({
  requested: 'AuthorizationRequested',
  resolved: 'AuthorizationResolved',
  expired: 'AuthorizationExpired',
});

export const DEFAULT_RECONNECT_DELAYS = Object.freeze([0, 2000, 5000, 10000, 30000]);

// Campos del contrato RequestAuthorizationContract: el contexto de UI jamas viaja crudo.
const REQUEST_CONTRACT_FIELDS = Object.freeze([
  'saleId',
  'productId',
  'productName',
  'quantity',
  'customUnitPriceUsd',
  'customUnitPriceLocal',
  'terminal',
]);

export function buildAuthorizationRequestPayload(context = {}) {
  const payload = {};
  for (const field of REQUEST_CONTRACT_FIELDS) {
    payload[field] = context[field] ?? null;
  }
  return payload;
}

export function createDefaultConnectionFactory({ signalRModule = signalR } = {}) {
  return ({ url, options }) => new signalRModule.HubConnectionBuilder()
    .withUrl(url, { withCredentials: options.withCredentials })
    .withAutomaticReconnect([...options.reconnectDelays])
    .configureLogging(signalRModule.LogLevel.Warning)
    .build();
}

export function createAuthorizationHubClient({
  connectionFactory = createDefaultConnectionFactory(),
  apiGet = (endpoint) => api.get(endpoint),
  apiPost = (endpoint, body) => api.post(endpoint, body),
  getBaseUrlFn = getBaseUrl,
  logger = console,
} = {}) {
  let connection = null;
  const eventHandlers = new Map();
  const reconnectedHandlers = new Set();

  function attachEventHandlers() {
    for (const [eventName, handlers] of eventHandlers) {
      for (const handler of handlers) connection.on(eventName, handler);
    }
  }

  function attachReconnectHandler() {
    connection.onreconnected(() => {
      // Las suscripciones viven en la conexion: sobreviven al reconnect. Los llamadores
      // usan onReconnected para recuperar estado via GET /api/authorizations/{id}.
      for (const handler of reconnectedHandlers) handler();
    });
  }

  async function connect() {
    if (connection) return connection;

    const hubUrl = `${getBaseUrlFn()}${AUTHORIZATION_HUB_PATH}`;
    connection = connectionFactory({
      url: hubUrl,
      options: { withCredentials: true, reconnectDelays: DEFAULT_RECONNECT_DELAYS },
    });

    attachEventHandlers();
    attachReconnectHandler();

    try {
      await connection.start();
    } catch (err) {
      // Socket caido no es fatal: los metodos delegan al par REST del backend.
      logger.warn('[AuthorizationHub] Error al conectar con el hub de autorizaciones:', err?.message);
    }

    return connection;
  }

  async function ensureConnected() {
    const conn = await connect();
    if (conn.state !== 'Connected') {
      try {
        await conn.start();
      } catch {
        // El estado final decide el fallback REST en cada metodo.
      }
    }
    return conn;
  }

  async function disconnect() {
    if (!connection) return;
    try {
      await connection.stop();
    } catch {
      // Ignorar errores al desconectar
    } finally {
      connection = null;
    }
  }

  function normalizeCreateResult(result) {
    return {
      requestId: result?.requestId ?? null,
      expiresAt: result?.expiresAt ?? null,
      status: result?.status ?? null,
      deduplicated: Boolean(result?.deduplicated),
      remainingLifetimeSeconds: result?.remainingLifetimeSeconds ?? 0,
    };
  }

  async function requestAuthorization(context) {
    const payload = buildAuthorizationRequestPayload(context);
    const conn = await ensureConnected();

    if (conn.state === 'Connected') {
      try {
        const result = await conn.invoke('RequestAuthorization', payload);
        if (result?.success === true) return normalizeCreateResult(result);
        throw new ApiError(result?.message || 'No se pudo crear la solicitud de autorización.', 400, result ?? null);
      } catch (err) {
        if (err instanceof ApiError) throw err;
        logger.warn('[AuthorizationHub] RequestAuthorization por socket fallo; usando REST:', err?.message);
      }
    }

    return normalizeCreateResult(await apiPost('/api/authorizations', payload));
  }

  // Expuesto para T8 (notificaciones admin): aprobar/rechazar con el mensaje exacto de carrera.
  async function resolveAuthorization(requestId, approved, reason = null) {
    const conn = await ensureConnected();

    if (conn.state === 'Connected') {
      try {
        const result = await conn.invoke('ResolveAuthorization', requestId, approved, reason);
        if (result?.success === true) {
          return {
            success: true,
            status: result.status ?? null,
            resolvedByName: result.resolvedByName ?? null,
            reason: result.reason ?? null,
          };
        }
        throw new ApiError(result?.message || 'No se pudo resolver la solicitud de autorización.', 409, result ?? null);
      } catch (err) {
        if (err instanceof ApiError) throw err;
        logger.warn('[AuthorizationHub] ResolveAuthorization por socket fallo; usando REST:', err?.message);
      }
    }

    const body = await apiPost(`/api/authorizations/${requestId}/resolve`, { approved, reason });
    return {
      success: true,
      status: body?.status ?? null,
      resolvedByName: body?.resolvedByName ?? null,
      reason: body?.reason ?? null,
    };
  }

  async function localResolve(requestId, { username, password, reason = null } = {}) {
    const body = await apiPost(`/api/authorizations/${requestId}/local-resolve`, { username, password, reason });
    return {
      token: body?.token ?? null,
      supervisorUserId: body?.supervisorUserId ?? null,
      supervisorName: body?.supervisorName ?? null,
      status: body?.status ?? null,
    };
  }

  async function getStatus(requestId) {
    return apiGet(`/api/authorizations/${requestId}`);
  }

  // 8.151 (W2, R4-client): retiro del solicitante por REST (el hub no expone metodo de
  // cancelacion). El backend responde 200 {requestId,status,resolvedAt}, 409 carrera/expirada,
  // 403 solo-solicitante o 404; los llamadores best-effort ignoran el rechazo.
  async function cancelRequest(requestId) {
    const body = await apiPost(`/api/authorizations/${requestId}/cancel`);
    return {
      success: true,
      status: body?.status ?? null,
      resolvedAt: body?.resolvedAt ?? null,
    };
  }

  function on(eventName, handler) {
    if (!eventHandlers.has(eventName)) eventHandlers.set(eventName, new Set());
    eventHandlers.get(eventName).add(handler);
    connection?.on(eventName, handler);
    return () => off(eventName, handler);
  }

  function off(eventName, handler) {
    eventHandlers.get(eventName)?.delete(handler);
    connection?.off(eventName, handler);
  }

  function onReconnected(handler) {
    reconnectedHandlers.add(handler);
    return () => reconnectedHandlers.delete(handler);
  }

  function isConnected() {
    return connection?.state === 'Connected';
  }

  return {
    connect,
    disconnect,
    requestAuthorization,
    resolveAuthorization,
    localResolve,
    getStatus,
    cancelRequest,
    on,
    off,
    onReconnected,
    isConnected,
  };
}

export const authorizationHub = createAuthorizationHubClient();

export function connectAuthorizationHub() {
  return authorizationHub.connect();
}

export function disconnectAuthorizationHub() {
  return authorizationHub.disconnect();
}