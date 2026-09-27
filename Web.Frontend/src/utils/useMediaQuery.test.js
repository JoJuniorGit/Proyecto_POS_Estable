import { describe, it, afterEach } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import { useMediaQuery } from './useMediaQuery.js';

const QUERY = '(max-width: 768px)';

function Probe() {
  const matches = useMediaQuery(QUERY);
  return React.createElement('span', null, matches ? 'MOBILE' : 'DESKTOP');
}

describe('useMediaQuery SSR-safe behavior', () => {
  const originalWindow = global.window;
  let capturedQuery = null;

  afterEach(() => {
    if (originalWindow === undefined) {
      delete global.window;
    } else {
      global.window = originalWindow;
    }
    capturedQuery = null;
  });

  it('1. Defaults to desktop when window is unavailable', () => {
    delete global.window;
    const html = renderToString(React.createElement(Probe));
    assert.match(html, /DESKTOP/);
    assert.doesNotMatch(html, /MOBILE/);
  });

  it('2. Defaults to desktop when matchMedia is unavailable', () => {
    global.window = {};
    const html = renderToString(React.createElement(Probe));
    assert.match(html, /DESKTOP/);
  });

  it('3. Reflects a matching media query on first render', () => {
    global.window = {
      matchMedia: (query) => {
        capturedQuery = query;
        return {
          matches: true,
          addEventListener: () => {},
          removeEventListener: () => {},
        };
      },
    };
    const html = renderToString(React.createElement(Probe));
    assert.match(html, /MOBILE/);
    assert.strictEqual(capturedQuery, QUERY);
  });

  it('4. Reflects a non-matching media query on first render', () => {
    global.window = {
      matchMedia: () => ({
        matches: false,
        addEventListener: () => {},
        removeEventListener: () => {},
      }),
    };
    const html = renderToString(React.createElement(Probe));
    assert.match(html, /DESKTOP/);
  });
});
