# async-cancellation-propagation Specification

## Purpose

Touched async endpoints and services accept a `CancellationToken` and propagate it down to EF Core
and the exchange-rate resolver, so an aborted request stops work instead of running to completion.
Also covers the WPF `async void OnClosing` handler, replaced by an exception-observing task path.

## Requirements

### Requirement: REQ-ACP-01 Controller Actions Accept and Propagate

Every touched async action MUST declare a `CancellationToken` parameter and pass the request's token
to the service call. Touched actions: `ShiftsController.CloseShift`, `GetCurrentReport`,
`GetReportById`; `CashDrawerController` actions plus its private `ResolveAnchoredRateAsync` and
`MapLocalTimesAsync`; `DailyClosureController.GetExpectedTotals`, `CreateClosure`, `GetClosure`.

#### Scenario: Token reaches the service

- GIVEN a request handled by a touched action
- WHEN the action invokes its service dependency
- THEN the call MUST receive the request cancellation token

#### Scenario: Aborted request stops work

- GIVEN a request whose token is cancelled before the service completes
- WHEN the action awaits the service
- THEN the cancellation MUST propagate as an operation-cancelled failure
- AND no drawer session or closure MUST be persisted for that request

### Requirement: REQ-ACP-02 Services Propagate to EF Core and the Rate Resolver

Async service methods — `DailyClosureService.GetExpectedTotalsByPaymentMethodAsync`,
`CreateClosureAsync`, `ExecuteClosureCoreAsync`, `GetClosureAsync`, `GetTodayExchangeRateAsync`, and
every async member of `CashDrawerService` — MUST accept and forward the token to EF Core calls and to
`ExchangeRateResolver`.

#### Scenario: Token reaches the EF query

- GIVEN a touched service method with a cancellation token
- WHEN it executes its data access
- THEN the token MUST be passed to the EF Core async call
- AND a cancelled token MUST stop the query instead of being ignored

#### Scenario: No sync-over-async is introduced

- GIVEN the touched async service paths
- WHEN they await data access
- THEN no `.Result`/`.Wait()`/`Thread.Sleep` blocking MUST be present on an async path

### Requirement: REQ-ACP-03 No async void in Touch Events

`Desktop.Client/MainWindow.xaml.cs:OnClosing` MUST NOT be `async void`. It MUST route the close work
through a task-returning handler that observes exceptions (e.g. `SafeFireAndForget`), and MUST
preserve the existing close behavior.

#### Scenario: OnClosing is not async void

- GIVEN the touched close handler
- WHEN its signature is inspected
- THEN it MUST NOT be `async void`
- AND failures from the close work MUST be observed and logged

#### Scenario: Close behavior is preserved

- GIVEN a window receiving a close request
- WHEN the handler runs
- THEN it MUST perform the same flush/dispose work as before
- AND an exception in that work MUST NOT leave the window in a half-closed state
