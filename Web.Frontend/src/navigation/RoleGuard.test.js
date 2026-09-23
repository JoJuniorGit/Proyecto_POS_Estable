import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import RoleGuard from './RoleGuard.jsx';
import AccessDenied from './AccessDenied.jsx';
import { AuthProvider } from '../context/AuthContext';

let profile = null;

globalThis.sessionStorage = {
  getItem: () => (profile ? JSON.stringify(profile) : null),
  setItem: () => {},
  removeItem: () => {},
  clear: () => {},
};

function renderGuard(role, view, message) {
  profile = role === undefined ? null : { id: 1, name: 'Test User', cedula: 'V-1', role };
  return renderToString(
    <AuthProvider>
      <RoleGuard view={view} message={message}>
        <span>SECURED_CONTENT</span>
      </RoleGuard>
    </AuthProvider>
  );
}

describe('RoleGuard [8.130]', () => {
  it('blocks a Driver from the closing view', () => {
    const html = renderGuard('Driver', 'closing');
    assert.match(html, /Acceso Denegado/);
    assert.doesNotMatch(html, /SECURED_CONTENT/);
  });

  it('blocks a Driver from catalog, pending, settings and exchange', () => {
    for (const view of ['catalog', 'pending', 'settings', 'exchange']) {
      const html = renderGuard('Driver', view);
      assert.match(html, /Acceso Denegado/, `Driver must be denied ${view}`);
      assert.doesNotMatch(html, /SECURED_CONTENT/, `Driver must not render ${view}`);
    }
  });

  it('allows an Admin into every routed view', () => {
    for (const view of ['pos', 'pending', 'pickups', 'catalog', 'history', 'register', 'closing', 'settings', 'exchange']) {
      const html = renderGuard(3, view);
      assert.match(html, /SECURED_CONTENT/, `Admin(3) must be allowed ${view}`);
      assert.doesNotMatch(html, /Acceso Denegado/, `Admin(3) must not be denied ${view}`);
    }
  });

  it('maps numeric backend roles to the same policy as names', () => {
    assert.match(renderGuard(1, 'closing'), /SECURED_CONTENT/);
    assert.doesNotMatch(renderGuard(1, 'settings'), /SECURED_CONTENT/);
    assert.match(renderGuard(2, 'settings'), /SECURED_CONTENT/);
    assert.match(renderGuard(4, 'pickups'), /SECURED_CONTENT/);
    assert.doesNotMatch(renderGuard(4, 'closing'), /SECURED_CONTENT/);
  });

  it('denies access when there is no authenticated role', () => {
    const html = renderGuard(null, 'pos');
    assert.match(html, /Acceso Denegado/);
    assert.doesNotMatch(html, /SECURED_CONTENT/);
  });

  it('forwards the page-specific denied message', () => {
    const html = renderGuard('Driver', 'register', 'No tienes permisos para ver el registro de caja.');
    assert.match(html, /No tienes permisos para ver el registro de caja\./);
  });

  it('AccessDenied renders the shared default message', () => {
    const html = renderToString(<AccessDenied />);
    assert.match(html, /Acceso Denegado/);
    assert.match(html, /No tienes los permisos necesarios para acceder a esta sección\./);
  });
});
