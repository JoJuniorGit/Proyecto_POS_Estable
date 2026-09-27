import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * S5c — H-08: RegisterPage paginates movements on the client (25 per page) and
 * reuses the existing `/api/cashdrawer/history?limit=` contract. No server
 * pagination contract (page/pageSize/offset) may be introduced (AD-19).
 */
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const pagePath = path.resolve(__dirname, '../../src/pages/RegisterPage.jsx');
const source = fs.readFileSync(pagePath, 'utf-8');

describe('RegisterPage — H-08 client-side pagination', () => {
  it('slices the filtered movements in memory (25 per page)', () => {
    const itemsPerPage = source.match(/const\s+ITEMS_PER_PAGE\s*=\s*(\d+)/);
    assert.ok(itemsPerPage, 'ITEMS_PER_PAGE must be defined');
    assert.equal(itemsPerPage[1], '25');

    assert.ok(
      /filteredTransactions\.slice\(/.test(source),
      'the movements table must slice the filtered list in memory'
    );
    assert.ok(
      /Math\.ceil\(filteredTransactions\.length\s*\/\s*ITEMS_PER_PAGE\)/.test(source),
      'totalPages must derive from the client-side filtered list'
    );
  });

  it('reuses the existing limit parameter and adds no server pagination contract', () => {
    const limitConstant = source.match(/const\s+HISTORY_FETCH_LIMIT\s*=\s*(\d+)/);
    assert.ok(limitConstant, 'HISTORY_FETCH_LIMIT must be defined');
    assert.ok(
      Number(limitConstant[1]) <= 300,
      'HISTORY_FETCH_LIMIT must stay within the server clamp (300)'
    );

    const historyCall = source.match(/api\.get\(`([^`]*\/api\/cashdrawer\/history[^`]*)`\)/);
    assert.ok(historyCall, 'RegisterPage must call GET /api/cashdrawer/history');
    const url = historyCall[1];
    assert.ok(
      url.includes('limit=${HISTORY_FETCH_LIMIT}'),
      'the history request must reuse the existing limit parameter'
    );

    assert.ok(!/[?&]page=/.test(url), 'the history request must not send page');
    assert.ok(!/pageSize|offset=/.test(url), 'the history request must not send pageSize/offset');
    assert.ok(
      !/\/api\/cashdrawer\/history\//.test(source),
      'no new paginated history endpoint may be introduced'
    );
  });
});
