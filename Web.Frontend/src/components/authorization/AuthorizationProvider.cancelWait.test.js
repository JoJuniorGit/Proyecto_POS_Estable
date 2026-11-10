// Residual 3 (8.188, S4): cierre de una espera concedida cuya ejecucion fallo. El modal de
// espera ofrece Cancelar en la fase Granted con error de reintento (paridad WPF): el operador no
// puede quedar atrapado. Con la accion en vuelo, cancelar sigue bloqueado.
import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'AuthorizationProvider.jsx'), 'utf-8');
const code = source.replace(/\/\/[^\n]*/g, '').replace(/\/\*[\s\S]*?\*\//g, '');

describe('AuthorizationProvider — cancelacion de la concesion con ejecucion fallida', () => {
  it('1. blocks the cancel only while the authorized action is in flight', () => {
    assert.match(
      code,
      /\(phase === AUTHORIZATION_PHASES\.GRANTED \|\| phase === AUTHORIZATION_PHASES\.LOCAL\)\s*&& isExecutingRef\.current\)\s*\{\s*return false;/,
      'cancelar debe no-op solo con la ejecucion autorizada en vuelo'
    );
  });

  it('2. settles a granted/local cancel through the pure settlement contract', () => {
    assert.match(
      code,
      /phase === AUTHORIZATION_PHASES\.GRANTED\s*\|\| phase === AUTHORIZATION_PHASES\.LOCAL\)\s*\{\s*settlePending\(getAuthorizationSettlement\(flowRef\.current, AUTHORIZATION_SETTLEMENTS\.MANUAL_CANCEL\)\)/,
      'la fase concedida debe liquidar con el settle de cancelacion'
    );
  });
});
