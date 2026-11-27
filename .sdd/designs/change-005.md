# change-005 Design - 8.212 WU4

## Flow
1. `CashDrawerViewModel.LoadSessionAsync` reads the active session and copies
   `PhysicalMethodBalances`; legacy sessions without the property call
   `GET /api/cashdrawer/physical-method-balances`.
2. When no session is active, the view model loads active physical payment methods and builds one
   `OpeningPhysicalMethodViewModel` per method.
3. The opening overlay sends explicit `MethodBalances` and derives `OpeningBalanceLocal` /
   `OpeningBalanceUsd` from the row currency codes.
4. The drawer view binds `PhysicalMethodBalances` to a wrapping card list.
5. `CashAdvanceRegisterViewModel` receives the physical methods, auto-selects the sole method or the
   first dispensable one, and computes the projected balance from the selected row.
6. Validation uses the selected method balance instead of the legacy local cap when physical methods
   exist; the exact server message shape is reproduced client-side.
7. `WpfDialogService` returns `CashAdvanceRegisterDialogResult` with the dispensed method, amount,
   and received method; `CashDrawerViewModel` forwards them through the method-aware client overload.
8. Every successful operation reloads the session, which refreshes the cards.

## Compatibility
The existing dialog contract and `ProcessCashAdvanceAsync` overload remain. New overloads have
default interface implementations that delegate to the legacy path. Sessions without method rows
keep the local/USD opening fields.

## Files
- `Desktop.Client.Core/Services/ICashDrawerService.cs`
- `Desktop.Client.Core/Services/CashDrawerService.cs`
- `Desktop.Client.Core/Services/IDialogService.cs`
- `Desktop.Client.Core/ViewModels/CashDrawerViewModel.cs`
- `Desktop.Client.Core/ViewModels/CashAdvanceRegisterViewModel.cs`
- `Desktop.Client.Core/ViewModels/OpeningPhysicalMethodViewModel.cs`
- `Desktop.Client/Views/CashDrawerView.xaml`
- `Desktop.Client/Views/CashAdvanceRegisterDialog.xaml`
- `Desktop.Client/Services/WpfDialogService.Modals.cs`

## Testing
ViewModel tests use Moq and the existing client DTOs. WPF XAML is compiled by the Release build.
The PostgreSQL lane is not required for this UI work unit.
