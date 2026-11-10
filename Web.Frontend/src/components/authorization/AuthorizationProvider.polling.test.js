// Residual 3 (8.188, S4): el provider monta el poller de estado del flujo de espera (fallback
// REST cuando el socket del hub no esta vivo). El efecto React no es ejercitable sin DOM, por lo
// que se fija el contrato de cableado sobre la fuente (misma convencion de los modales).
import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'AuthorizationProvider.jsx'), 'utf-8');

describe('AuthorizationProvider — fallback REST del flujo de espera', () => {
  it('1. mounts the status poller only while a wait is live and stops it on cleanup', () => {
    assert.match(source, /createAuthorizationStatusPoller/);
    assert.match(source, /poller\.start\(\)/);
    assert.match(source, /return \(\) => poller\.stop\(\)/);
    assert.match(source, /if \(flow\.phase !== AUTHORIZATION_PHASES\.WAITING\) return undefined;/);
  });

  it('2. polls only when the hub socket is not connected', () => {
    assert.match(source, /isHubConnected: \(\) => authorizationHub\.isConnected\(\)/);
  });
});
