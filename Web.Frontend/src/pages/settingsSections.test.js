import { describe, it } from 'node:test';
import assert from 'node:assert';
import { SETTINGS_SECTIONS, resolveTabIndex } from './settingsSections.js';

describe('settingsSections', () => {
  it('defines the four existing web domains in order', () => {
    assert.deepStrictEqual(
      SETTINGS_SECTIONS.map((section) => section.id),
      ['currency', 'payments', 'network', 'system']
    );
    assert.deepStrictEqual(
      SETTINGS_SECTIONS.map((section) => section.label),
      [
        'Moneda, Tasas y Formato',
        'Métodos de Pago y Caja',
        'Conectividad y Red Local',
        'Sistema y Mantenimiento',
      ]
    );
  });

  it('wraps arrow navigation and handles Home/End', () => {
    assert.strictEqual(resolveTabIndex(0, 'ArrowRight'), 1);
    assert.strictEqual(resolveTabIndex(3, 'ArrowRight'), 0);
    assert.strictEqual(resolveTabIndex(0, 'ArrowLeft'), 3);
    assert.strictEqual(resolveTabIndex(2, 'ArrowUp'), 1);
    assert.strictEqual(resolveTabIndex(1, 'ArrowDown'), 2);
    assert.strictEqual(resolveTabIndex(2, 'Home'), 0);
    assert.strictEqual(resolveTabIndex(1, 'End'), 3);
  });

  it('ignores unrelated keys and invalid indexes', () => {
    assert.strictEqual(resolveTabIndex(1, 'Enter'), null);
    assert.strictEqual(resolveTabIndex(-1, 'ArrowRight'), null);
    assert.strictEqual(resolveTabIndex(4, 'ArrowRight'), null);
  });
});
