import { AUTHORIZATION_ACTION_TYPES } from './authorizationHub.js';
import {
  PETTY_CASH_CATEGORY_OPTIONS,
  PETTY_CASH_CURRENCY_OPTIONS,
  buildPettyCashAuthorizationMessage,
  extractPettyCashAuthorizationContext,
  isPettyCashAuthorizationError,
} from './pettyCashApi.js';

/**
 * Residual 3 (8.188, S4/S5): flujo web completo de autorizacion de caja chica.
 *
 * El backend responde 403 con el contexto exacto y congelado de la operacion (sesion, divisa,
 * montos bimonetarios y categoria). Este modulo lo traduce al contexto canonico del hub/REST
 * (misma clave `actionType` que el cliente WPF), abre la espera del `AuthorizationProvider`
 * (SignalR con fallback REST + timeout) y reintenta UNA sola vez con el token aprobado y la
 * misma `Idempotency-Key` del intento original. Sin reintentos ciegos y fallo cerrado: si el
 * contexto no es utilizable, no se crea solicitud y se informa al operador.
 */

const TERMINAL_FALLBACK = 'Web';

/**
 * Identidad de terminal de esta caja web (espeja el `Environment.MachineName` de WPF): el
 * hostname del navegador es visible para el supervisor en la cola; sin el, una etiqueta estable.
 */
export function getWebTerminalLabel(scope = typeof window !== 'undefined' ? window : null) {
  const hostname = scope?.location?.hostname;
  return typeof hostname === 'string' && hostname.length > 0 ? hostname : TERMINAL_FALLBACK;
}

function isKnownCurrency(value) {
  return PETTY_CASH_CURRENCY_OPTIONS.some((option) => option.value === value);
}

function isKnownCategory(value) {
  return PETTY_CASH_CATEGORY_OPTIONS.some((option) => option.value === value);
}

/**
 * Traduce `authorizationContext` del 403 al contexto canonico que el hub/REST canonicalizan.
 * Fail-closed: devuelve null cuando falta un campo o no pertenece al catalogo del contrato, de
 * modo que jamas se crea una solicitud con un contexto distinto al que el servidor exigira.
 * @returns {{actionType: number, pettyCashSessionId: number, pettyCashCurrency: number,
 *   pettyCashAmountBsS: number, pettyCashAmountUsd: number, pettyCashCategory: number,
 *   terminal: string|null}|null}
 */
export function buildPettyCashExpenseAuthorizationContext(authorizationContext, { terminal = null } = {}) {
  if (!authorizationContext || typeof authorizationContext !== 'object') return null;

  const sessionId = Number(authorizationContext.sessionId);
  const currency = Number(authorizationContext.currency);
  const category = Number(authorizationContext.category);
  const amountBss = Number(authorizationContext.amountBss ?? authorizationContext.amountBsS);
  const amountUsd = Number(authorizationContext.amountUsd);

  if (!Number.isInteger(sessionId) || sessionId <= 0) return null;
  if (!isKnownCurrency(currency)) return null;
  if (!isKnownCategory(category)) return null;
  if (!Number.isFinite(amountBss) || amountBss <= 0) return null;
  if (!Number.isFinite(amountUsd) || amountUsd <= 0) return null;

  return {
    actionType: AUTHORIZATION_ACTION_TYPES.PettyCashExpense,
    pettyCashSessionId: sessionId,
    pettyCashCurrency: currency,
    pettyCashAmountBsS: amountBss,
    pettyCashAmountUsd: amountUsd,
    pettyCashCategory: category,
    terminal,
  };
}

/** Mensajes de liquidacion de la espera (paridad con los textos del dialogo WPF). */
const SETTLEMENT_MESSAGES = Object.freeze({
  rejected: 'La autorización fue rechazada.',
  expired: 'La solicitud de autorización expiró. Intente nuevamente.',
  cancelled: 'La autorización fue cancelada.',
  unavailable: 'No se pudo completar la operación autorizada.',
  requestFailed: 'No se pudo iniciar la solicitud de autorización.',
});

function describeSettlement(outcome = {}) {
  if (outcome?.cancelled) return SETTLEMENT_MESSAGES.cancelled;

  if (outcome?.outcome === 'rejected') {
    return outcome.reason
      ? `La autorización fue rechazada: ${outcome.reason}`
      : SETTLEMENT_MESSAGES.rejected;
  }

  if (outcome?.outcome === 'expired') return SETTLEMENT_MESSAGES.expired;

  return SETTLEMENT_MESSAGES.unavailable;
}

/**
 * Intenta la operacion protegida de caja chica y, ante el 403 del contrato, abre la espera de
 * autorizacion y reintenta una unica vez con el token aprobado.
 *
 * @param {object} dependencies
 * @param {(options: {authorizationToken?: string, idempotencyKey?: string}) => Promise<any>} dependencies.submit
 *   Accion protegida (registro o anulacion). El primer intento viaja sin token.
 * @param {(request: {context: object, retry: (token: string) => Promise<any>}) => Promise<object>} dependencies.startWait
 *   Espera bloqueante del AuthorizationProvider (SignalR + fallback REST + timeout + cancelacion).
 * @param {() => string} [dependencies.getIdempotencyKey] La anulacion no maneja clave; el registro
 *   la exige. Se invoca UNA vez por intento logico: el reintento autorizado comparte el valor.
 * @param {string|null} [dependencies.terminal] Etiqueta de terminal visible para el supervisor.
 * @param {(value: number) => string} [dependencies.formatBsS] Formato opcional del mensaje fallback.
 * @param {(value: number) => string} [dependencies.formatUSD] Formato opcional del mensaje fallback.
 * @returns {Promise<{ok: true, result: any} | {ok: false, message: string}>}
 *   Los errores ajenos al contrato de autorizacion se propagan al caller.
 */
export async function submitPettyCashWithAuthorization({
  submit,
  startWait,
  getIdempotencyKey = null,
  terminal = null,
  formatBsS = undefined,
  formatUSD = undefined,
} = {}) {
  if (typeof submit !== 'function' || typeof startWait !== 'function') {
    throw new Error('submitPettyCashWithAuthorization requiere { submit, startWait }.');
  }

  // El intento logico nace con su clave: ambos envios (inicial y autorizado) la comparten para
  // que el servidor resuelva el replay si la respuesta se pierde.
  const idempotencyKey = typeof getIdempotencyKey === 'function' ? (getIdempotencyKey() ?? null) : null;
  const attemptOptions = idempotencyKey ? { idempotencyKey } : {};

  let actionResult;
  try {
    actionResult = await submit(attemptOptions);
    return { ok: true, result: actionResult };
  } catch (error) {
    if (!isPettyCashAuthorizationError(error)) throw error;

    const context = buildPettyCashExpenseAuthorizationContext(
      extractPettyCashAuthorizationContext(error),
      { terminal }
    );

    if (!context) {
      return {
        ok: false,
        message: buildPettyCashAuthorizationMessage(error, { formatBsS, formatUSD }),
      };
    }

    let retriedResult;
    let retried = false;
    try {
      const outcome = await startWait({
        context,
        retry: async (token) => {
          retriedResult = await submit({
            ...(idempotencyKey ? { idempotencyKey } : {}),
            authorizationToken: token,
          });
          retried = true;
          return retriedResult;
        },
      });

      if (outcome?.ok === true && retried) {
        return { ok: true, result: retriedResult };
      }

      return { ok: false, message: describeSettlement(outcome) };
    } catch (error2) {
      return {
        ok: false,
        message: error2?.message || SETTLEMENT_MESSAGES.requestFailed,
      };
    }
  }
}
