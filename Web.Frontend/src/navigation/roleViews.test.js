import { describe, it } from 'node:test';
import assert from 'node:assert';
import {
  VIEW_ORDER,
  ROLE_VIEWS,
  normalizeRole,
  getAllowedViews,
  isValidView,
  canAccessView,
  getFallbackView,
  resolveAccessibleView,
} from './roleViews.js';

const ALL_VIEWS = [...VIEW_ORDER];

const EXPECTED_POLICY = {
  Admin: ALL_VIEWS,
  Manager: ALL_VIEWS,
  Cashier: ['pos', 'pending', 'pickups', 'catalog', 'history', 'register', 'closing'],
  Driver: ['pickups', 'history'],
};

describe('roleViews role→view matrix [8.130]', () => {
  it('declares every routed view once and no duplicate grants per role', () => {
    assert.strictEqual(new Set(VIEW_ORDER).size, VIEW_ORDER.length);
    for (const role of Object.keys(ROLE_VIEWS)) {
      assert.strictEqual(new Set(ROLE_VIEWS[role]).size, ROLE_VIEWS[role].length);
    }
  });

  for (const [role, allowed] of Object.entries(EXPECTED_POLICY)) {
    it(`${role} is allowed exactly in: ${allowed.join(', ')}`, () => {
      for (const view of VIEW_ORDER) {
        assert.strictEqual(canAccessView(role, view), allowed.includes(view), `${role} → ${view}`);
        assert.strictEqual(getAllowedViews(role).includes(view), allowed.includes(view), `${role} allowedViews → ${view}`);
      }
    });
  }

  it('keeps Driver blocked from sales, cash and closures', () => {
    for (const view of ['pos', 'register', 'closing']) {
      assert.strictEqual(canAccessView('Driver', view), false, `Driver → ${view}`);
    }
  });

  it('normalizes role names case-insensitively and trims whitespace', () => {
    assert.strictEqual(normalizeRole('Admin'), 'Admin');
    assert.strictEqual(normalizeRole('  manager '), 'Manager');
    assert.strictEqual(normalizeRole('CASHIER'), 'Cashier');
    assert.strictEqual(normalizeRole('driver'), 'Driver');
  });

  it('normalizes backend UserRole numeric values (1=Cashier, 2=Manager, 3=Admin, 4=Driver)', () => {
    assert.strictEqual(normalizeRole(1), 'Cashier');
    assert.strictEqual(normalizeRole(2), 'Manager');
    assert.strictEqual(normalizeRole(3), 'Admin');
    assert.strictEqual(normalizeRole(4), 'Driver');
    assert.strictEqual(normalizeRole('4'), 'Driver');
    assert.strictEqual(canAccessView(3, 'settings'), true);
    assert.strictEqual(canAccessView(1, 'settings'), false);
    assert.strictEqual(canAccessView(4, 'pos'), false);
  });

  it('denies unknown or missing roles by default', () => {
    for (const role of [null, undefined, '', 0, 5, 99, 'Supervisor', 'root', {}]) {
      assert.strictEqual(normalizeRole(role), null, `normalizeRole(${String(role)})`);
      assert.deepStrictEqual(getAllowedViews(role), []);
      for (const view of VIEW_ORDER) {
        assert.strictEqual(canAccessView(role, view), false, `${String(role)} → ${view}`);
      }
    }
  });

  it('validates view identifiers against the canonical list', () => {
    for (const view of VIEW_ORDER) {
      assert.strictEqual(isValidView(view), true);
    }
    for (const view of ['', 'nope', 'POS', null, undefined, 'admin']) {
      assert.strictEqual(isValidView(view), false, `isValidView(${String(view)})`);
    }
  });

  it('falls back to the first allowed view per role', () => {
    assert.strictEqual(getFallbackView('Admin'), 'pos');
    assert.strictEqual(getFallbackView('Manager'), 'pos');
    assert.strictEqual(getFallbackView('Cashier'), 'pos');
    assert.strictEqual(getFallbackView('Driver'), 'pickups');
    assert.strictEqual(getFallbackView('Unknown'), null);
  });

  it('keeps allowed views untouched', () => {
    assert.deepStrictEqual(resolveAccessibleView('Cashier', 'catalog'), { allowed: true, view: 'catalog' });
    assert.deepStrictEqual(resolveAccessibleView('Driver', 'pickups'), { allowed: true, view: 'pickups' });
  });

  it('redirects denied hashes to the role fallback', () => {
    assert.deepStrictEqual(resolveAccessibleView('Driver', 'closing'), { allowed: false, view: 'pickups' });
    assert.deepStrictEqual(resolveAccessibleView('Driver', 'catalog'), { allowed: false, view: 'pickups' });
    assert.deepStrictEqual(resolveAccessibleView('Driver', 'pending'), { allowed: false, view: 'pickups' });
    assert.deepStrictEqual(resolveAccessibleView('Cashier', 'settings'), { allowed: false, view: 'pos' });
    assert.deepStrictEqual(resolveAccessibleView('Cashier', 'exchange'), { allowed: false, view: 'pos' });
    assert.deepStrictEqual(resolveAccessibleView('Admin', 'nope'), { allowed: false, view: 'pos' });
  });

  it('denies the app when the role has no allowed views', () => {
    assert.deepStrictEqual(resolveAccessibleView(null, 'pos'), { allowed: false, view: null });
    assert.deepStrictEqual(resolveAccessibleView('Supervisor', 'closing'), { allowed: false, view: null });
  });

  it('re-evaluates the active view when the role changes', () => {
    assert.deepStrictEqual(resolveAccessibleView('Cashier', 'settings'), { allowed: false, view: 'pos' });
    assert.deepStrictEqual(resolveAccessibleView('Manager', 'settings'), { allowed: true, view: 'settings' });
    assert.deepStrictEqual(resolveAccessibleView('Driver', 'settings'), { allowed: false, view: 'pickups' });
  });

  it('a Driver opening #closing never keeps the denied view active', () => {
    const requested = 'closing';
    const access = resolveAccessibleView('Driver', requested);
    const activeView = access.allowed ? requested : access.view;
    assert.strictEqual(access.allowed, false);
    assert.strictEqual(activeView, 'pickups');
    assert.strictEqual(canAccessView('Driver', activeView), true);
  });
});
