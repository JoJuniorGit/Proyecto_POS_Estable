import * as signalR from '@microsoft/signalr';
import { getBaseUrl } from './api';

let connection = null;

/**
 * Conecta al Hub de SignalR para recibir actualizaciones de la tasa de cambio en tiempo real.
 * @param {function(number): void} onRateUpdate - Callback ejecutado al recibir una nueva tasa
 */
export async function connectRateHub(onRateUpdate) {
  if (connection) {
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
