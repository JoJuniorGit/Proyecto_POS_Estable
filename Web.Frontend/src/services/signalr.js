import * as signalR from '@microsoft/signalr';
import { getBaseUrl } from './api';

let connection = null;

// 8.157 (SEC-07, REQ-HFC-02): evento exacto que el servidor empuja al grupo user:{id} cuando
// revoca la sesión (InvalidateUserSessionsAsync) sobre ambos hubs.
export const FORCE_DISCONNECT_EVENT = 'ForceDisconnect';

// 8.157: la notificación de revocación reutiliza el mecanismo existente de sesión expirada:
// api.js emite 'pos_unauthorized' ante un 401 y AuthContext lo escucha para limpiar la sesión
// en memoria y volver al login. Como la revocación la origina el servidor (sello invalidado),
// aquí no se repite el POST /api/auth/logout: la próxima llamada REST rechazada completará
// ese flujo 401 existente.
export const SESSION_REVOKED_EVENT = 'pos_unauthorized';

export function notifySessionRevoked() {
  if (typeof window !== 'undefined') {
    window.dispatchEvent(new CustomEvent(SESSION_REVOKED_EVENT));
  }
}

/**
 * 8.157 (SEC-07, REQ-HFC-02): registra el manejo de ForceDisconnect sobre la conexión del hub
 * de tasa. El stop explícito impide que withAutomaticReconnect reactive el socket y el patrón
 * off/on evita handlers duplicados cuando rewireHandlers corre en cada connectRateHub.
 * Exportado para poder probarlo con una conexión fake.
 * @param {object} conn - Conexión SignalR (o fake con off/on/stop).
 * @param {object} [options] - onRevoked/logger inyectables; defaults de producción.
 */
export function handleForceDisconnect(conn, { onRevoked = notifySessionRevoked, logger = console } = {}) {
  conn.off(FORCE_DISCONNECT_EVENT);
  conn.on(FORCE_DISCONNECT_EVENT, () => {
    logger.warn('[SignalR] Sesión revocada por el servidor; cerrando conexión de tasa en tiempo real.');
    Promise.resolve()
      .then(() => conn.stop())
      .catch(() => {})
      .finally(() => {
        // Liberar la conexión módulo para que un futuro connectRateHub reconstruya el socket
        // en vez de devolver esta conexión ya detenida.
        if (connection === conn) connection = null;
        onRevoked();
      });
  });
}

// 8.6-M2: cada mensaje del hub produce UN SOLO dispatch. Los callbacks registrados por
// ExchangeRateProvider ya despachan el CustomEvent de ventana (onHoldSalesUpdated,
// onPaymentMethodsUpdated). Aquí NO se despacha de nuevo, para evitar doble notificación.
const rewireHandlers = (onRateUpdate, onHoldSalesUpdated, onPaymentMethodsUpdated) => {
  // ReceiveRateUpdate se registra una sola vez en la construcción (ver abajo).
  if (onHoldSalesUpdated) {
    connection.off('OnHoldSalesUpdated');
    connection.on('OnHoldSalesUpdated', () => onHoldSalesUpdated());
  }
  if (onPaymentMethodsUpdated) {
    connection.off('OnPaymentMethodsUpdated');
    connection.on('OnPaymentMethodsUpdated', () => onPaymentMethodsUpdated());
  }

  // 8.6-M4: off/on explícito para la tasa en re-conexiones evita suscriptores acumulados.
  connection.off('ReceiveRateUpdate');
  connection.on('ReceiveRateUpdate', (newRate) => {
    if (typeof newRate === 'number' && onRateUpdate) {
      onRateUpdate(newRate);
    }
  });

  connection.off('OnCurrencyFormatUpdated');
  connection.on('OnCurrencyFormatUpdated', (newFormat) => {
    if (newFormat) {
      window.dispatchEvent(new CustomEvent('onCurrencyFormatUpdated', { detail: newFormat }));
    }
  });

  // 8.157 (SEC-07, REQ-HFC-02): la revocación del servidor detiene este hub.
  handleForceDisconnect(connection);
};

/**
 * Conecta al Hub de SignalR para recibir actualizaciones de la tasa de cambio, ventas en espera y métodos de pago en tiempo real.
 * @param {function(number): void} onRateUpdate - Callback ejecutado al recibir una nueva tasa
 * @param {function(): void} [onHoldSalesUpdated] - Callback ejecutado cuando se recalculan las ventas en espera
 * @param {function(): void} [onPaymentMethodsUpdated] - Callback ejecutado cuando cambian los métodos de pago
 * @param {object} [options] - Opciones de infraestructura (inyección usada por el harness de tests).
 * @param {function({url: string}): object} [options.connectionFactory] - Factory alternativa de la conexión;
 *   por defecto construye el HubConnection real con auto-reconnect y logging Warning.
 */
export async function connectRateHub(onRateUpdate, onHoldSalesUpdated, onPaymentMethodsUpdated, { connectionFactory } = {}) {
  if (connection) {
    rewireHandlers(onRateUpdate, onHoldSalesUpdated, onPaymentMethodsUpdated);
    return connection;
  }

  const hubUrl = `${getBaseUrl()}/hubs/exchange-rate`;

  connection = connectionFactory
    ? connectionFactory({ url: hubUrl })
    : new signalR.HubConnectionBuilder()
        .withUrl(hubUrl, { withCredentials: true })
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .configureLogging(signalR.LogLevel.Warning)
        .build();

  rewireHandlers(onRateUpdate, onHoldSalesUpdated, onPaymentMethodsUpdated);

  try {
    await connection.start();
  } catch (err) {
    console.warn('[SignalR] Error al conectar con Hub de Tasa de Cambio:', err.message);
  }

  return connection;
}

/**
 * Desconecta el Hub de SignalR.
 */
export async function disconnectRateHub() {
  if (connection) {
    try {
      await connection.stop();
    } catch {
      // Ignorar errores al desconectar
    } finally {
      connection = null;
    }
  }
}