import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import HoldSaleModal from './HoldSaleModal.jsx';

describe('HoldSaleModal real mount', () => {
  it('renders to string without throwing (idempotency key holder wiring intact)', () => {
    let html = '';
    assert.doesNotThrow(() => {
      html = renderToString(
        <HoldSaleModal
          isOpen
          onClose={() => {}}
          saleId={7}
          currentCustomer={null}
          saleTotalUSD={100}
          saleTotalBsS={5000}
          exchangeRate={50}
          onSuccess={() => {}}
        />
      );
    }, 'HoldSaleModal must render without ReferenceError');

    assert.ok(html.length > 0, 'rendered HTML must not be empty');
  });
});
