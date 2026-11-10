// 8.187-T5 (Paso 11/12, F4): navegación por rol de la página CxC web.
// La caja cobra: Admin/Manager/Cashier acceden; Driver nunca.
import { describe, it } from 'node:test';
import assert from 'node:assert';
import {
  VIEW_ORDER,
  canAccessView,
  getFallbackView,
  isValidView,
  resolveAccessibleView,
} from './roleViews.js';

describe('roleViews crédito CxC [Paso 11/12 T5]', () => {
  it('1. registers credit as a routed, restorable view', () => {
    assert.ok(VIEW_ORDER.includes('credit'), "'credit' debe declararse en VIEW_ORDER");
    assert.strictEqual(isValidView('credit'), true);
  });

  it('2. grants CxC to Admin, Manager and Cashier (la caja cobra), never to Driver', () => {
    assert.strictEqual(canAccessView('Admin', 'credit'), true);
    assert.strictEqual(canAccessView('Manager', 'credit'), true);
    assert.strictEqual(canAccessView('Cashier', 'credit'), true);
    assert.strictEqual(canAccessView('Driver', 'credit'), false);
    assert.strictEqual(canAccessView(3, 'credit'), true);
    assert.strictEqual(canAccessView(4, 'credit'), false);
  });

  it('3. keeps the existing fallbacks and redirects denied roles away from CxC', () => {
    assert.deepStrictEqual(resolveAccessibleView('Cashier', 'credit'), { allowed: true, view: 'credit' });
    assert.deepStrictEqual(resolveAccessibleView('Driver', 'credit'), { allowed: false, view: 'pickups' });
    assert.strictEqual(getFallbackView('Driver'), 'pickups');
    assert.strictEqual(getFallbackView('Cashier'), 'pos');
    assert.strictEqual(getFallbackView('Admin'), 'pos');
  });
});
