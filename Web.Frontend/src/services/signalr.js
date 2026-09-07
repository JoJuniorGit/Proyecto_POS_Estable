import * as signalR from '@microsoft/signalr';
import { getBaseUrl } from './api';

let connection = null;

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
};

/**
 * Conecta al Hub de SignalR para recibir actualizaciones de la tasa de cambio, ventas en espera y métodos de pago en tiempo real.
 * @param {function(number): void} onRateUpdate - Callback ejecutado al recibir una nueva tasa
 * @param {function(): void} [onHoldSalesUpdated] - Callback ejecutado cuando se recalculan las ventas en espera
 * @param {function(): void} [onPaymentMethodsUpdated] - Callback ejecutado cuando cambian los métodos de pago
 */
export async function connectRateHub(onRateUpdate, onHoldSalesUpdated, onPaymentMethodsUpdated) {
  if (connection) {
    rewireHandlers(onRateUpdate, onHoldSalesUpdated, onPaymentMethodsUpdated);
    return connection;
  }

  const hubUrl = `${getBaseUrl()}/hubs/exchange-rate`;

  connection = new signalR.HubConnectionBuilder()
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