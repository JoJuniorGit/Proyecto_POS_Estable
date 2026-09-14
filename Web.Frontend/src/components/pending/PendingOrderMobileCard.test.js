import { describe, test } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import PendingOrderMobileCard from './PendingOrderMobileCard.jsx';
import { getLockInfo } from '../../utils/holdLock';
import { buildPendingSale } from '../../../test/pendingSaleFixture';

function render(overrides) {
  const sale = buildPendingSale(overrides);
  const lockInfo = getLockInfo(sale, 1);
  return renderToString(
    React.createElement(PendingOrderMobileCard, {
      sale,
      isExpanded: false,
      lockInfo,
      isElevated: overrides?.isElevated ?? false,
      exchangeRate: 847.44,
      onToggle: () => {},
      onCheckout: () => {},
      onEdit: () => {},
      onForceRelease: () => {},
    })
  );
}

describe('PendingOrderMobileCard renderToString', () => {
  test('Render_ConBloqueoAjeno_MuestraBadgeYDeshabilitaAcciones', () => {
    const html = render({
      claimedByUserId: 99,
      claimedByUserName: 'María Pérez',
      claimAction: 'Checkout',
      isElevated: false,
    });
    assert.match(html, /Bloqueado por María Pérez/);
    assert.match(html, /disabled/);
  });

  test('Render_ConBloqueoPropio_MuestraBloqueadoPorTiYHabilitaAcciones', () => {
    const html = render({
      claimedByUserId: 1,
      claimedByUserName: 'Test User',
      claimAction: 'Checkout',
    });
    assert.match(html, /Bloqueado por ti/);
    assert.ok(!html.includes('disabled'), 'buttons should not be disabled for own lock');
  });

  test('Render_SinReclamo_NoMuestraBadgeYHabilitaAcciones', () => {
    const html = render({
      claimedByUserId: null,
      claimedByUserName: null,
      claimAction: null,
    });
    assert.ok(!html.includes('Bloqueado'), 'should not show badge when free');
    assert.ok(!html.includes('disabled'), 'buttons should not be disabled when free');
  });

  test('Render_ConAdminYBloqueoAjeno_MuestraLiberar', () => {
    const html = render({
      claimedByUserId: 99,
      claimedByUserName: 'María',
      claimAction: 'Checkout',
      isElevated: true,
    });
    assert.match(html, /Liberar Pedido/);
  });

  test('Render_ConCajeroYBloqueoAjeno_NoMuestraLiberar', () => {
    const html = render({
      claimedByUserId: 99,
      claimedByUserName: 'María',
      claimAction: 'Checkout',
      isElevated: false,
    });
    assert.ok(!html.includes('Liberar'), 'Cashier should not see Liberar button');
  });
});
