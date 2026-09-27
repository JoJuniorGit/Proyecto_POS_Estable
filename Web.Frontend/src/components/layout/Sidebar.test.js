import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import Sidebar from './Sidebar.jsx';
import { AuthProvider } from '../../context/AuthContext';
import { ThemeProvider } from '../../context/ThemeContext';

let profile = null;

globalThis.localStorage = {
  getItem: () => null,
  setItem: () => {},
  removeItem: () => {},
  clear: () => {},
};

globalThis.sessionStorage = {
  getItem: () => (profile ? JSON.stringify(profile) : null),
  setItem: () => {},
  removeItem: () => {},
  clear: () => {},
};

const ALL_LABELS = [
  'Punto de Venta',
  'Cuentas Abiertas',
  'Retiros Pendientes',
  'Catálogo',
  'Historial Ventas',
  'Caja',
  'Cierre Diario',
  'Configuración',
  'Tasa de Cambio',
];

const EXPECTED_LABELS = {
  Admin: ALL_LABELS,
  Manager: ALL_LABELS,
  Cashier: ['Punto de Venta', 'Cuentas Abiertas', 'Retiros Pendientes', 'Catálogo', 'Historial Ventas', 'Caja', 'Cierre Diario'],
  Driver: ['Retiros Pendientes', 'Historial Ventas'],
};

function renderSidebar(role) {
  profile = role === undefined ? null : { id: 1, name: 'Test User', cedula: 'V-1', role };
  const html = renderToString(
    <ThemeProvider>
      <AuthProvider>
        <Sidebar currentView="pos" onNavigate={() => {}} isOpen={false} onClose={() => {}} />
      </AuthProvider>
    </ThemeProvider>
  );
  return html.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ');
}

describe('Sidebar per-role menu [8.130]', () => {
  for (const [role, labels] of Object.entries(EXPECTED_LABELS)) {
    it(`${role} shows exactly the mapped menu`, () => {
      const text = renderSidebar(role);
      for (const label of ALL_LABELS) {
        assert.strictEqual(text.includes(label), labels.includes(label), `${role} → "${label}"`);
      }
    });
  }

  it('numeric backend roles produce the same menu as their names', () => {
    const adminText = renderSidebar(3);
    const managerText = renderSidebar(2);
    const cashierText = renderSidebar(1);
    const driverText = renderSidebar(4);

    assert.ok(adminText.includes('Configuración'));
    assert.ok(managerText.includes('Configuración'));
    assert.ok(!cashierText.includes('Configuración'));
    assert.ok(!cashierText.includes('Tasa de Cambio'));
    assert.ok(!driverText.includes('Punto de Venta'));
    assert.ok(!driverText.includes('Caja'));
    assert.ok(driverText.includes('Retiros Pendientes'));
    assert.ok(driverText.includes('Historial Ventas'));
  });

  it('shows no menu items when there is no role', () => {
    const text = renderSidebar(undefined);
    for (const label of ALL_LABELS) {
      assert.strictEqual(text.includes(label), false, `anonymous → "${label}"`);
    }
  });
});
