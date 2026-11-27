# change-007 Design - 8.212 WU6

## Concurrency test
`PostgresCashAdvanceMethodBalanceConcurrencyTests` uses `IsolatedPostgresDatabase` and two
`CashAdvanceCoordinator` instances with independent `SalesDbContext` connections. A shared
`TaskCompletionSource` releases both requests together. Both target the same local physical method
with an amount that makes the combined request exceed the opening balance. The assertions cover the
winner/loser result, persisted sale and movements, and the final per-method balance.

## Notifier test
`SignalRCashDrawerNotifierTests` mocks `IHubContext<ExchangeRateHub>`, `IHubClients`, and
`IClientProxy`, invokes the notifier, and verifies `SendCoreAsync("CashDrawerChanged", [sessionId])`.
The controller already calls the notifier after the service operation returns; this test locks the
transport contract.

## E2E harness
`E2eApiClient` gains:
- `OpenCashDrawerSessionWithMethodsAsync(...)` returning the session id and physical balances.
- `GetActiveCashDrawerSessionAsync(...)` and `GetPhysicalMethodBalancesAsync(...)`.
- `ProcessCashAdvanceWithMethodAsync(...)`.

`FullStackPhysicalMethodDrawerTests` runs against the real API and isolated PostgreSQL: open with
explicit method balances, verify two physical rows, process an advance from the local method, and
verify the local balance decreased while the USD method stayed unchanged. The test returns silently
when the E2E stack is unavailable, matching the existing full-stack harness contract.

## Files
- `CommandCenter.Tests/Integration/PostgresCashAdvanceMethodBalanceConcurrencyTests.cs`
- `CommandCenter.Tests/Unit/SignalRCashDrawerNotifierTests.cs`
- `tests/CommandCenter.Wpf.E2ETests/Fixtures/E2eApiClient.cs`
- `tests/CommandCenter.Wpf.E2ETests/Tests/FullStackPhysicalMethodDrawerTests.cs`

## Testing
Run the isolated PostgreSQL lane (`pwsh scripts/test-postgres-lane.ps1`), focused unit tests, the
full no-PG suite, and the normal .NET/web builds. E2E remains gated by `E2E_POSTGRES_CONNECTION`.
