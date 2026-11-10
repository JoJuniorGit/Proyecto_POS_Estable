// 8.188 (Paso 11-13): segregación de funciones del crédito en el POS web. El rol decide si el
// panel crediticio expone el formulario de edición; el cajero solo accede a métricas de lectura.
// El formulario completo (cupo, plazo, gracia, notas y bloqueo manual) se reserva a Admin/Manager.
import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import {
  canEditCreditProfileByRole,
  canManageCreditProfileForm,
} from './creditLogic.js';
import CreditProfilePanel from './CreditProfilePanel.jsx';

describe('creditLogic — RBAC del perfil crediticio', () => {
  it('1. only Admin/Manager can edit the credit profile (canonical and numeric aliases)', () => {
    assert.strictEqual(canEditCreditProfileByRole('Admin'), true);
    assert.strictEqual(canEditCreditProfileByRole('Manager'), true);
    assert.strictEqual(canEditCreditProfileByRole('admin'), true);
    assert.strictEqual(canEditCreditProfileByRole('3'), true); // alias numérico Admin
    assert.strictEqual(canEditCreditProfileByRole('2'), true); // alias numérico Manager
    assert.strictEqual(canEditCreditProfileByRole('Cashier'), false);
    assert.strictEqual(canEditCreditProfileByRole('1'), false); // alias numérico Cashier
    assert.strictEqual(canEditCreditProfileByRole('Driver'), false);
    assert.strictEqual(canEditCreditProfileByRole(''), false);
    assert.strictEqual(canEditCreditProfileByRole(null), false);
    assert.strictEqual(canEditCreditProfileByRole(undefined), false);
  });

  it('2. the cashier never gets the editable form (hidden, not merely disabled)', () => {
    const profile = { creditStatus: 0 };

    assert.strictEqual(canManageCreditProfileForm({ canEdit: false, profile }), false);
    assert.strictEqual(canManageCreditProfileForm({ canEdit: true, profile }), true);
    assert.strictEqual(canManageCreditProfileForm({ canEdit: true, profile, isDefaultCustomer: true }), false);
    assert.strictEqual(canManageCreditProfileForm({ canEdit: true, profile, isSystemSuspended: true }), false);
    assert.strictEqual(canManageCreditProfileForm({ canEdit: true, profile: null }), false);
    assert.strictEqual(canManageCreditProfileForm(), false);
  });

  it('3. the read-only panel never emits credit edit controls', () => {
    const html = renderToString(
      <CreditProfilePanel customer={{ id: 7, name: 'Ana Pérez' }} canEdit={false} />
    );

    assert.doesNotMatch(html, /Guardar Perfil/);
    assert.doesNotMatch(html, /Bloqueo manual/);
    assert.doesNotMatch(html, /Línea de crédito habilitada/);
    assert.doesNotMatch(html, /Cupo autorizado \(USD\)/);
  });

  it('4. the default customer is restricted instead of offering credit fields', () => {
    const html = renderToString(
      <CreditProfilePanel customer={{ id: 1, name: 'Consumidor Final', isDefault: true }} canEdit />
    );

    assert.match(html, /Consumidor Final/);
    assert.doesNotMatch(html, /Guardar Perfil/);
  });
});
