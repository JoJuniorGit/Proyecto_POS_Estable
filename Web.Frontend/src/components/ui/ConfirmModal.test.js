import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import ConfirmModal from './ConfirmModal.jsx';

describe('ConfirmModal accessible name wiring', () => {
  it('1. Two instances render unique aria-labelledby targets pointing at their own titles', () => {
    const html = renderToString(
      <div>
        <ConfirmModal isOpen onClose={() => {}} onConfirm={() => {}} title="First" />
        <ConfirmModal isOpen onClose={() => {}} onConfirm={() => {}} title="Second" />
      </div>
    );

    const labelledBy = [...html.matchAll(/aria-labelledby="([^"]+)"/g)].map((match) => match[1]);
    assert.strictEqual(labelledBy.length, 2);
    assert.notStrictEqual(labelledBy[0], labelledBy[1]);

    for (const id of labelledBy) {
      const occurrences = html.split(`id="${id}"`).length - 1;
      assert.strictEqual(occurrences, 1, `title id ${id} must appear exactly once`);
    }
  });

  it('2. Renders nothing while closed', () => {
    const html = renderToString(
      <ConfirmModal isOpen={false} onClose={() => {}} onConfirm={() => {}} />
    );
    assert.strictEqual(html, '');
  });
});
