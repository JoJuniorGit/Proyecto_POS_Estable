import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import BarcodeScannerHud from './BarcodeScannerHud.jsx';
import {
  shouldFallbackWithoutDeviceId,
  cameraPermissionGuidance,
  queryCameraPermission,
  resolveCameraGuidance,
  friendlyCameraError,
} from '../../utils/scannerCameraErrors.js';

function withNavigator(navigatorValue, fn) {
  const ownNavigator = Object.prototype.hasOwnProperty.call(globalThis, 'navigator');
  let prev;
  if (ownNavigator) {
    prev = Object.getOwnPropertyDescriptor(globalThis, 'navigator');
  }
  Object.defineProperty(globalThis, 'navigator', { value: navigatorValue, configurable: true, writable: true });
  try {
    return fn();
  } finally {
    if (prev) Object.defineProperty(globalThis, 'navigator', prev);
    else delete globalThis.navigator;
  }
}

describe('BarcodeScanner permission recovery pipeline [8.109]', () => {
  it('1. Falls back without a stale deviceId on OverconstrainedError or NotFoundError', () => {
    assert.strictEqual(shouldFallbackWithoutDeviceId('OverconstrainedError', true), true);
    assert.strictEqual(shouldFallbackWithoutDeviceId('NotFoundError', true), true);
    assert.strictEqual(shouldFallbackWithoutDeviceId('DevicesNotFoundError', true), true);
    assert.strictEqual(shouldFallbackWithoutDeviceId('NotAllowedError', true), false);
    assert.strictEqual(shouldFallbackWithoutDeviceId('OverconstrainedError', false), false);
  });

  it('2. Denied state guides to unlock the browser padlock', () => {
    const guidance = cameraPermissionGuidance('denied', { name: 'NotAllowedError' });
    assert.match(guidance.text, /bloqueada/);
    assert.match(guidance.text, /candado/);
    assert.strictEqual(guidance.reloadHint, false);
  });

  it('3. Prompt state guides to accept the browser dialog', () => {
    const guidance = cameraPermissionGuidance('prompt', { name: 'NotAllowedError' });
    assert.match(guidance.text, /acepte el permiso/);
    assert.strictEqual(guidance.reloadHint, false);
  });

  it('4. Granted plus NotAllowedError recommends retry and reload as last resort', () => {
    const guidance = cameraPermissionGuidance('granted', { name: 'NotAllowedError' });
    assert.match(guidance.text, /Reintentar/);
    assert.strictEqual(guidance.reloadHint, true);
  });

  it('5. Unsupported state falls back to friendly camera error without reload hint', () => {
    const guidance = cameraPermissionGuidance('unsupported', { name: 'NotReadableError' });
    assert.match(guidance.text, /ocupada/);
    assert.strictEqual(guidance.reloadHint, false);
  });

  it('6. queryCameraPermission resolves unsupported without navigator.permissions', async () => {
    await withNavigator({ permissions: undefined }, async () => {
      assert.strictEqual(await queryCameraPermission(), 'unsupported');
    });
  });

  it('7. queryCameraPermission resolves denied from the permissions API', async () => {
    await withNavigator({
      permissions: {
        query: async ({ name }) => ({ state: name === 'camera' ? 'denied' : 'prompt' }),
      },
    }, async () => {
      assert.strictEqual(await queryCameraPermission(), 'denied');
    });
  });

  it('8. resolveCameraGuidance combines permission state with the error name', async () => {
    await withNavigator({
      permissions: {
        query: async () => ({ state: 'granted' }),
      },
    }, async () => {
      const guidance = await resolveCameraGuidance({ name: 'NotAllowedError' });
      assert.strictEqual(guidance.reloadHint, true);
    });
  });

  it('9. friendlyCameraError maps permission, missing and busy camera errors', () => {
    assert.match(friendlyCameraError({ name: 'NotAllowedError', message: 'x' }), /Permiso denegado/);
    assert.match(friendlyCameraError({ name: 'NotFoundError', message: 'x' }), /No se encontró ninguna cámara/);
    assert.match(friendlyCameraError({ name: 'NotReadableError', message: 'x' }), /ocupada/);
    assert.strictEqual(friendlyCameraError(null), 'No se pudo iniciar la cámara.');
  });

  it('10. Hud error overlay renders Reintentar and Recargar página buttons', () => {
    const html = renderToString(
      React.createElement(BarcodeScannerHud, {
        videoRef: { current: null },
        isOpen: true,
        isLaptop: false,
        starting: false,
        status: { type: 'error', text: 'boom' },
        insecureContextMessage: 'inseguro',
        boundingBoxRef: { current: [] },
        onRetry: () => {},
        reloadHint: true,
      })
    );
    assert.match(html, /Reintentar/);
    assert.match(html, /Recargar página/);
  });

  it('11. Hud hides retry buttons on insecure-context message', () => {
    const html = renderToString(
      React.createElement(BarcodeScannerHud, {
        videoRef: { current: null },
        isOpen: true,
        isLaptop: true,
        starting: false,
        status: { type: 'error', text: 'inseguro' },
        insecureContextMessage: 'inseguro',
        boundingBoxRef: { current: [] },
        onRetry: () => {},
        reloadHint: true,
      })
    );
    assert.doesNotMatch(html, /Reintentar/);
    assert.doesNotMatch(html, /Recargar página/);
  });

  it('12. Hud hides retry and reload when no onRetry is bound', () => {
    const html = renderToString(
      React.createElement(BarcodeScannerHud, {
        videoRef: { current: null },
        isOpen: true,
        isLaptop: false,
        starting: false,
        status: { type: 'error', text: 'boom' },
        insecureContextMessage: 'inseguro',
        boundingBoxRef: { current: [] },
      })
    );
    assert.doesNotMatch(html, /Reintentar/);
    assert.doesNotMatch(html, /Recargar página/);
  });
});
