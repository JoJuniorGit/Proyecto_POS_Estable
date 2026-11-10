// 8.211-T4b1 (S9): lectura del perfil monetario de presentación para clientes autenticados.
// Es un GET sin mutación; el guardado sigue en /api/settings/monetary-profile (Admin).
import { api } from './api';

/**
 * @param {AbortSignal} [signal]
 * @returns {Promise<object | null>}
 */
export function getMonetaryProfile(signal) {
  return api.get('/api/client/monetary-profile', signal);
}
