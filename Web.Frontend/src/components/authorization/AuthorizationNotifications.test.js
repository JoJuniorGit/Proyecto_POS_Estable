import { describe, it } from 'node:test';
import assert from 'node:assert';
import React from 'react';
import { renderToString } from 'react-dom/server';
import {
  AUTHORIZATION_ACTION_LABELS,
  AUTHORIZATION_NOTIFICATION_STRINGS,
  AuthorizationNotificationModal,
  AuthorizationNotificationNotice,
  applyAuthorizationExpiredEvent,
  applyAuthorizationResolvedEvent,
  applyResolveAuthorizationOutcome,
  buildAlreadyResolvedMessage,
  buildAuthorizationDetail,
  buildAuthorizationNotificationMessage,
  enqueueAuthorizationNotification,
  removeAuthorizationNotification,
} from './AuthorizationNotifications.jsx';

const EXPIRES_AT = '2026-10-08T12:00:30.000Z';

const REQUEST_A = {
  requestId: 1,
  actionType: 'ManualPriceOverride',
  saleId: 445,
  requestedByName: 'Cajero 01',
  terminal: 'Caja-01',
  context: {
    productId: 5,
    productName: 'Café molido',
    quantity: 2,
    customUnitPriceUsd: 1.5,
    customUnitPriceLocal: 55.5,
  },
  createdAt: '2026-10-08T11:59:30.000Z',
  expiresAt: EXPIRES_AT,
};

const REQUEST_B = { ...REQUEST_A, requestId: 2, saleId: 446, requestedByName: 'Cajero 02' };
const REQUEST_C = { ...REQUEST_A, requestId: 3, saleId: 447, requestedByName: 'Cajero 03' };

function buildQueue() {
  let queue = [];
  for (const request of [REQUEST_A, REQUEST_B, REQUEST_C]) {
    queue = enqueueAuthorizationNotification(queue, request);
  }
  return queue;
}

describe('AuthorizationNotifications message builders', () => {
  it('1. builds the enriched D7 message with cashier, action, sale and context detail', () => {
    assert.strictEqual(
      buildAuthorizationNotificationMessage(REQUEST_A),
      'El cajero Cajero 01 solicita autorización para modificar el precio manual en la Factura #445.'
        + ' Producto: Café molido × 2. Precio USD: $ 1.50. Precio Bs.S: 55.50'
    );
    assert.strictEqual(AUTHORIZATION_ACTION_LABELS.ManualPriceOverride, 'modificar el precio manual');
  });

  it('2. degrades gracefully when cashier, sale id or context are missing', () => {
    assert.strictEqual(
      buildAuthorizationNotificationMessage({ actionType: 'ManualPriceOverride' }),
      'Un cajero solicita autorización para modificar el precio manual.'
    );
    assert.strictEqual(
      buildAuthorizationNotificationMessage({ requestedByName: 'Cajero 01', actionType: 'ManualPriceOverride' }),
      'El cajero Cajero 01 solicita autorización para modificar el precio manual.'
    );
    assert.strictEqual(
      buildAuthorizationNotificationMessage({ requestedByName: 'Cajero 01', actionType: 'SaleCancellation', saleId: 12 }),
      'El cajero Cajero 01 solicita autorización para SaleCancellation en la Factura #12.'
    );
  });

  it('3. builds the exact race message from the resolver name', () => {
    assert.strictEqual(
      buildAlreadyResolvedMessage('Admin Uno'),
      'Esta solicitud ya fue resuelta por Admin Uno.'
    );
    assert.strictEqual(
      buildAlreadyResolvedMessage(),
      'Esta solicitud ya fue resuelta por otro usuario.'
    );
  });

  it('4. formats the display context detail from the hub payload', () => {
    assert.strictEqual(
      buildAuthorizationDetail({ productName: 'Café molido', quantity: 2, customUnitPriceUsd: 1.5, customUnitPriceLocal: 55.5 }),
      'Producto: Café molido × 2. Precio USD: $ 1.50. Precio Bs.S: 55.50'
    );
    assert.strictEqual(buildAuthorizationDetail(null), '');
  });
});

describe('AuthorizationNotifications queue reducer', () => {
  it('5. queues multiple simultaneous requests in arrival order without losing any', () => {
    const queue = buildQueue();

    assert.deepStrictEqual(queue.map((item) => item.requestId), [1, 2, 3]);
    assert.strictEqual(queue[0].message, buildAuthorizationNotificationMessage(REQUEST_A));
    assert.strictEqual(queue[1].terminal, 'Caja-01');
    assert.strictEqual(queue[2].expiresAt, EXPIRES_AT);
  });

  it('6. ignores malformed payloads and dedupes the same request id', () => {
    const empty = [];
    assert.strictEqual(enqueueAuthorizationNotification(empty, null), empty);
    assert.strictEqual(enqueueAuthorizationNotification(empty, { requestId: null }), empty);

    const once = enqueueAuthorizationNotification(empty, REQUEST_A);
    assert.strictEqual(enqueueAuthorizationNotification(once, REQUEST_A), once);
  });

  it('7. removes the resolved request and surfaces the race message for someone else resolution', () => {
    const queue = buildQueue();

    const result = applyAuthorizationResolvedEvent(queue, {
      requestId: 2,
      status: 'Approved',
      resolvedByName: 'Admin Uno',
    });

    assert.deepStrictEqual(result.queue.map((item) => item.requestId), [1, 3]);
    assert.strictEqual(result.notice, 'Esta solicitud ya fue resuelta por Admin Uno.');
  });

  it('8. closes an own resolution silently', () => {
    const queue = buildQueue();

    const ownEvent = applyAuthorizationResolvedEvent(
      queue,
      { requestId: 2, status: 'Approved', resolvedByName: 'Admin Uno' },
      { isOwnResolution: true }
    );
    assert.deepStrictEqual(ownEvent.queue.map((item) => item.requestId), [1, 3]);
    assert.strictEqual(ownEvent.notice, null);

    const ownOutcome = applyResolveAuthorizationOutcome(queue, {
      requestId: 1,
      success: true,
      status: 'Approved',
      resolvedByName: 'Admin Uno',
    });
    assert.deepStrictEqual(ownOutcome.queue.map((item) => item.requestId), [2, 3]);
    assert.strictEqual(ownOutcome.notice, null);
  });

  it('9. ignores resolved events for requests that were never queued', () => {
    const queue = buildQueue();

    const result = applyAuthorizationResolvedEvent(queue, {
      requestId: 99,
      status: 'Approved',
      resolvedByName: 'Admin Uno',
    });

    assert.deepStrictEqual(result.queue.map((item) => item.requestId), [1, 2, 3]);
    assert.strictEqual(result.notice, null);
  });

  it('10. removes expired requests with the expired note', () => {
    const queue = buildQueue();

    const result = applyAuthorizationExpiredEvent(queue, { requestId: 1, status: 'Expired' });

    assert.deepStrictEqual(result.queue.map((item) => item.requestId), [2, 3]);
    assert.strictEqual(result.notice, AUTHORIZATION_NOTIFICATION_STRINGS.expired);

    const unknown = applyAuthorizationExpiredEvent(queue, { requestId: 99, status: 'Expired' });
    assert.deepStrictEqual(unknown.queue.map((item) => item.requestId), [1, 2, 3]);
    assert.strictEqual(unknown.notice, null);
  });

  it('11. prefers the server message when a late resolve loses the race', () => {
    const queue = buildQueue();

    const lost = applyResolveAuthorizationOutcome(queue, {
      requestId: 1,
      success: false,
      status: 'Approved',
      resolvedByName: 'Admin Dos',
      message: 'Esta solicitud ya fue resuelta por Admin Dos.',
    });
    assert.deepStrictEqual(lost.queue.map((item) => item.requestId), [2, 3]);
    assert.strictEqual(lost.notice, 'Esta solicitud ya fue resuelta por Admin Dos.');

    const fallback = applyResolveAuthorizationOutcome(queue, {
      requestId: 1,
      success: false,
      resolvedByName: 'Admin Dos',
    });
    assert.strictEqual(fallback.notice, 'Esta solicitud ya fue resuelta por Admin Dos.');

    const noInfo = applyResolveAuthorizationOutcome(queue, { requestId: 1, success: false });
    assert.deepStrictEqual(noInfo.queue.map((item) => item.requestId), [2, 3]);
    assert.strictEqual(noInfo.notice, null);
  });

  it('12. removes a single request by id', () => {
    const queue = buildQueue();

    assert.deepStrictEqual(removeAuthorizationNotification(queue, 2).map((item) => item.requestId), [1, 3]);
  });
});

describe('AuthorizationNotifications components (structural, SSR)', () => {
  it('13. renders the interrupting modal with enriched copy, countdown and approve/reject actions', () => {
    const html = renderToString(React.createElement(AuthorizationNotificationModal, {
      notification: {
        requestId: 1,
        message: buildAuthorizationNotificationMessage(REQUEST_A),
        terminal: 'Caja-01',
        expiresAt: EXPIRES_AT,
      },
      queuedCount: 3,
      remainingSeconds: 30,
      onApprove: () => {},
      onReject: () => {},
    }));

    assert.match(html, /role="dialog"/);
    assert.match(html, /aria-modal="true"/);
    assert.match(html, /Solicitud de autorización/);
    assert.match(html, /El cajero Cajero 01 solicita autorización para modificar el precio manual en la Factura #445\./);
    assert.match(html, /Caja-01/);
    assert.match(html, /00:30/);
    assert.match(html, /3 solicitudes pendientes/);
    assert.match(html, /Aprobar/);
    assert.match(html, /Rechazar/);
    assert.match(html, /Motivo \(opcional\)/);
  });

  it('14. hides the queue badge for a single request and renders nothing when closed', () => {
    const single = renderToString(React.createElement(AuthorizationNotificationModal, {
      notification: { requestId: 1, message: 'El cajero Cajero 01 solicita autorización para modificar el precio manual.', terminal: null, expiresAt: null },
      queuedCount: 1,
      remainingSeconds: 0,
      onApprove: () => {},
      onReject: () => {},
    }));
    assert.ok(!single.includes('solicitudes pendientes'));

    assert.strictEqual(
      renderToString(React.createElement(AuthorizationNotificationModal, { notification: null })),
      ''
    );
  });

  it('15. renders the exact race message in the dismissible notice', () => {
    const html = renderToString(React.createElement(AuthorizationNotificationNotice, {
      notice: 'Esta solicitud ya fue resuelta por Admin Uno.',
      onDismiss: () => {},
    }));

    assert.match(html, /role="alert"/);
    assert.match(html, /Esta solicitud ya fue resuelta por Admin Uno\./);

    assert.strictEqual(
      renderToString(React.createElement(AuthorizationNotificationNotice, { notice: null })),
      ''
    );
  });
});
