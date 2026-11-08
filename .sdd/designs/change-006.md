# change-006 Design - 8.212 WU5

## Flow
1. `cashDrawerApi.js` exposes the physical-method balance read and the method-aware advance request.
2. `RegisterPage` loads the active session and physical balances together, stores them in state, and
   renders a card grid.
3. `CashAdvanceModal` receives the balances, resolves the selected dispenser, projects the remaining
   balance, validates client-side, and posts the extended request.
4. `CashDrawerOpenModal` fetches active physical payment methods, filters them to the profile
   currencies, renders one amount input per method, and derives the legacy aggregates before posting
   `methodBalances`.
5. `CashDrawerCloseModal` receives the same balances as read-only theoretical rows.
6. `signalr.js` registers `CashDrawerChanged` and dispatches a window event; `RegisterPage` listens
   and calls `loadSession`.
7. Backend `SignalRCashDrawerNotifier` sends `CashDrawerChanged(sessionId)`; controllers call it
   only after the service operation returns, i.e. after commit.

## Compatibility
The session DTO keeps the legacy local/USD fields. The web modal keeps the legacy amount/electronic
fields and only adds the dispenser fields when a physical method is selected.

## Files
- `Web.Frontend/src/services/cashDrawerApi.js`
- `Web.Frontend/src/services/cashDrawerApi.test.js`
- `Web.Frontend/src/pages/RegisterPage.jsx`
- `Web.Frontend/src/components/register/CashAdvanceModal.jsx`
- `Web.Frontend/src/components/register/CashDrawerOpenModal.jsx`
- `Web.Frontend/src/components/register/CashDrawerCloseModal.jsx`
- `Web.Frontend/src/services/signalr.js`
- `Backend.API/Services/SignalRCashDrawerNotifier.cs`
- `Backend.API/Startup/ServiceCollectionExtensions.cs`
- `Backend.API/Controllers/CashDrawerController.cs`
- `Backend.API/Controllers/PettyCashController.cs`
- `Sales.Module/Interfaces/ICashDrawerChangedNotifier.cs`

## Testing
Use the existing node:test harness for pure helpers and SSR render assertions. Keep the existing
tests green; add focused coverage for balance cards, advance validation, opening payload, and the
SignalR event registration.
