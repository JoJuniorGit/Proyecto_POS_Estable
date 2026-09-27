import { describe, it } from 'node:test';
import assert from 'node:assert';
import { shouldRecoverClosedShift } from './shiftRecovery.js';

describe('shouldRecoverClosedShift decision table', () => {
  it('1. Recovers when the backend message reports an already closed shift', () => {
    assert.strictEqual(shouldRecoverClosedShift('El turno ya fue cerrado', 400), true);
    assert.strictEqual(shouldRecoverClosedShift('TURNO CERRADO', undefined), true);
  });

  it('2. Recovers on HTTP 400 even without the closed-shift wording', () => {
    assert.strictEqual(shouldRecoverClosedShift('Solicitud inválida', 400), true);
  });

  it('3. Recovers on HTTP 409 conflict', () => {
    assert.strictEqual(shouldRecoverClosedShift('', 409), true);
  });

  it('4. Does not recover for other statuses or messages', () => {
    assert.strictEqual(shouldRecoverClosedShift('Error interno', 500), false);
    assert.strictEqual(shouldRecoverClosedShift('No autorizado', 401), false);
    assert.strictEqual(shouldRecoverClosedShift('Error 503: Service Unavailable', 503), false);
  });

  it('5. Fails closed for missing or non-string messages without a recovery status', () => {
    assert.strictEqual(shouldRecoverClosedShift(undefined, undefined), false);
    assert.strictEqual(shouldRecoverClosedShift(null, null), false);
    assert.strictEqual(shouldRecoverClosedShift('', 0), false);
    assert.strictEqual(shouldRecoverClosedShift({ message: 'cerrado' }, undefined), false);
  });
});
