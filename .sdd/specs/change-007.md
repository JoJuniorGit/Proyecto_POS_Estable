# change-007 Specification - 8.212 WU6

## Requirements

### R1. Concurrent method-aware advance
Against a real PostgreSQL database, two cash advances that each request more than half of the same
physical method balance must not both succeed. The advisory lock acquired by
`LockOpenSessionForWriteAsync` must serialize the balance re-read and validation.

### R2. Loser has no effect
The losing advance must fail with the exact insufficient-balance domain error and persist no sale,
no advance expense, and no advance income. The winning advance must persist exactly one sale and the
two expected drawer movements.

### R3. Balance invariant
After the race, the physical method balance must equal opening minus the winning amount and must
never be negative. The aggregate local balance must stay consistent with the per-method balance.

### R4. Post-commit notification
`SignalRCashDrawerNotifier` must send `CashDrawerChanged` with the session id through the
`ExchangeRateHub`. Controllers call it only after the drawer service returns, never inside the
advisory lock.

### R5. Full-stack E2E
The E2E harness must be able to open a drawer with an explicit `methodBalances` breakdown, read the
active session's `physicalMethodBalances`, process a method-aware advance, and read the updated
per-method balances. The scenario is gated by the existing `E2E_POSTGRES_CONNECTION` contract.

### R6. Regression evidence
The change adds focused tests without weakening existing coverage. Build, web tests, the isolated
PostgreSQL lane, and the known no-PG baseline must remain green.
