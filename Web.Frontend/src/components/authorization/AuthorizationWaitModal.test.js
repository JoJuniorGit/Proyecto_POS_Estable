import { describe, it } from 'node:test';
import assert from 'node:assert';
import React from 'react';
import { renderToString } from 'react-dom/server';
import AuthorizationWaitModal from './AuthorizationWaitModal.jsx';
import { AUTHORIZATION_STRINGS } from '../../services/authorizationFlow.js';

const CONTEXT = {
  saleId: 445,
  productId: 5,
  productName: 'Café molido',
  quantity: 2,
  customUnitPriceUsd: 1.5,
  customUnitPriceLocal: 55.5,
};

function render(overrides = {}) {
  return renderToString(React.createElement(AuthorizationWaitModal, {
    isOpen: true,
    phase: 'waiting',
    context: CONTEXT,
    remainingSeconds: 30,
    onCancel: () => {},
    onOpenLocalForm: () => {},
    onCloseLocalForm: () => {},
    onLocalSubmit: () => {},
    onRetry: () => {},
    onRetryExecution: () => {},
    ...overrides,
  }));
}

describe('AuthorizationWaitModal (structural, SSR)', () => {
  it('1. renders the blocking wait state with exact copy, context and countdown', () => {
    const html = render();

    assert.match(html, /role="dialog"/);
    assert.match(html, /aria-modal="true"/);
    assert.match(html, /Esperando autorización remota\.\.\./);
    assert.match(html, /Factura/);
    assert.match(html, /#445/);
    assert.match(html, /Café molido/);
    assert.match(html, /00:30/);
    assert.match(html, /Autorización Local/);
    assert.match(html, /Cancelar/);
    assert.ok(!html.includes(AUTHORIZATION_STRINGS.rejected), 'waiting state must not show the rejection copy');
  });

  it('2. opens the supervisor credential form and keeps the wait state on invalid credentials', () => {
    const html = render({ isLocalFormOpen: true, localError: AUTHORIZATION_STRINGS.invalidCredentials });

    assert.match(html, /Esperando autorización remota\.\.\./);
    assert.match(html, /placeholder="Usuario"/);
    assert.match(html, /placeholder="Contraseña"/);
    assert.match(html, /Credenciales inválidas o sin privilegios para autorizar\./);
    assert.match(html, /Autorizar/);
    assert.match(html, /Cancelar/);
  });

  it('3. shows the rejection outcome with its optional reason', () => {
    const html = render({ phase: 'rejected', reason: 'Monto fuera de política' });

    assert.match(html, /Solicitud rechazada\./);
    assert.match(html, /Monto fuera de política/);
    assert.ok(!html.includes('Esperando autorización remota...'), 'rejection must replace the wait headline');
  });

  it('4. shows the expiry outcome with a retry action that starts a new request', () => {
    const html = render({ phase: 'expired' });

    assert.match(html, /Expirada/);
    assert.match(html, /Reintentar/);
    assert.match(html, /Cancelar/);
  });

  it('4b. surfaces a failed retry request on the expired state', () => {
    const html = render({ phase: 'expired', retryError: 'No se pudo conectar con el servidor POS.' });

    assert.match(html, /Expirada/);
    assert.match(html, /No se pudo conectar con el servidor POS\./);
    assert.match(html, /Reintentar/);
  });

  it('5. surfaces a failed token retry with the retry action', () => {
    const html = render({ phase: 'granted', retryError: 'No se pudo conectar con el servidor POS.' });

    assert.match(html, /No se pudo conectar con el servidor POS\./);
    assert.match(html, /Reintentar/);
    assert.match(html, /Cancelar/);
  });

  it('6. renders nothing while closed', () => {
    assert.strictEqual(render({ isOpen: false }), '');
  });
});