import * as signalR from '@microsoft/signalr';
import { getBaseUrl } from './api';

let connection = null;

/**
 * Conecta al Hub de SignalR para recibir actualizaciones de la tasa de cambio, ventas en espera y métodos de pago en tiempo real.
 * @param {function(number): void} onRateUpdate - Callback ejecutado al recibir una nueva tasa
 * @param {function(): void} [onHoldSalesUpdated] - Callback ejecutado cuando se recalculan las ventas en espera
 * @param {function(): void} [onPaymentMethodsUpdated] - Callback ejecutado cuando cambian los métodos de pago
 */
export async function connectRateHub(onRateUpdate, onHoldSalesUpdated, onPaymentMethodsUpdated) {
  if (connection) {
    if (onHoldSalesUpdated) {
      connection.off('OnHoldSalesUpdated');
      connection.on('OnHoldSalesUpdated', () => {
        onHoldSalesUpdated();
        window.dispatchEvent(new CustomEvent('onHoldSalesUpdated'));
      });
    }
    if (onPaymentMethodsUpdated) {
      connection.off('OnPaymentMethodsUpdated');
      connection.on('OnPaymentMethodsUpdated', () => {
        onPaymentMethodsUpdated();
        window.dispatchEvent(new CustomEvent('onPaymentMethodsUpdated'));
      });
    }
    return connection;
  }

  const hubUrl = `${getBaseUrl()}/hubs/exchange-rate`;

  connection = new signalR.HubConnectionBuilder()
    .withUrl(hubUrl)
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  connection.on('ReceiveRateUpdate', (newRate) => {
    if (typeof newRate === 'number' && onRateUpdate) {
      onRateUpdate(newRate);
    }
  });

  connection.on('OnHoldSalesUpdated', () => {
    if (onHoldSalesUpdated) {
      onHoldSalesUpdated();
    }
    window.dispatchEvent(new CustomEvent('onHoldSalesUpdated'));
  });

  connection.on('OnPaymentMethodsUpdated', () => {
    if (onPaymentMethodsUpdated) {
      onPaymentMethodsUpdated();
    }
    window.dispatchEvent(new CustomEvent('onPaymentMethodsUpdated'));
  });

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
