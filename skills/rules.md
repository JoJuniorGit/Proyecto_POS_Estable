# Rules

1.  Person and Strict Boundaries

- Mandatory Flow (No Vibe Coding): BEFORE modifying any code, generate a plan in Markdown format and wait for my approval. Do not assume unspecified business rules; stop and ask.
- Boundaries: - NEVER modify the database schema without first performing an EF Core migration.
- No comments: NEVER use comments unless I ask for them directly
- snake_case: ALWAYS use "snake_case" to name variables
- No Code-Behind: NEVER use Code-Behind (`.xaml.cs`). Everything must be handled using ViewModels or Behaviors.

2.  Project Commands (Run these to validate your work)

- Compile: `dotnet build`
- Generate Migration: `dotnet ef migrations add <MigrationName> --project src/Infrastructure`
- Update Database: `dotnet ef database update --project src/Infrastructure`

3.  Domain Business Rules (CRITICAL)

- Tax-Free System: It is prohibited to write, calculate, or reference VAT, IVA, or taxes in the database, backend, or UI. The price is based 100% on: `Cost * (1 + (Margin / 100))`.
- Currency Handling (Bs.S and USD):
- USD (Dollars): Use decimal with a maximum of 2 decimal places.
- Cash (Immutable Flow): Cash on hand is stored with the exact physical amount in Bs.S. Do not recalculate past values ​​by multiplying by historical exchange rates.
- Time Zones: Store dates in the database in `UTC`. When mapping to the DTO, convert to `America/Caracas` (or the time zone configured in `SystemSettings`).

4.  Architecture and Code Style

- Stack: .NET 10, WPF, CommunityToolkit.Mvvm, PostgreSQL, EF Core.
- Frontend (WPF): - Minimalist aesthetic: Displays Bs.S as large numbers, without a currency symbol. Displays USD in small, faint text.
- Use `VirtualizingStackPanel` for decimal lists.
- In search bars, use a 100ms debounce and the `IsSearching` visual state.
- Backend and API:
- All I/O must be strictly asynchronous (`async`/`await`). Do not use `.Result` or `Task.Wait()`.
- ALWAYS use `CancellationToken`.
- Use `AsNoTracking()` for reads that do not require updating.
- DTO Pattern: NEVER send EF Core entities to the view. Use DTOs to avoid `IgnoreCycles` in JSON responses.

5. Update and Mapping Protocol (Anti-Breakage)
   To avoid mapping errors and desynchronization when creating or editing endpoints/functions, the agent MUST follow these steps in order:

a. Impact Analysis:

- Before creating an endpoint, check if a DTO already exists that fulfills the function. Do not duplicate transfer objects.

- If a field is added to an Entity, the agent must find ALL references to that entity and update their corresponding DTOs.

b. Mandatory Mapping Chain (The Data Pipeline):

- The data flow is: `Database -> EF Entity -> Service -> DTO -> Controller -> UI`.

- Inflexible Rule: If the agent modifies one end of the chain, it is REQUIRED to update the intermediate links. Jumping from the Service to the Controller using anonymous types or the pure entity is not allowed.

c. Contract Verification:

- When updating an existing endpoint, verify the `Model` used by the WPF view. If you change the name of a property in the API's JSON, the agent must correct the Binding in the XAML or the DTO in the C# client.

5. Code Patterns
   Correct: Calculated Property in ViewModel

```csharp
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(SellingPriceBsS))]
private decimal _cost_price;

[ObservableProperty]
[NotifyPropertyChangedFor(nameof(SellingPriceBsS))]
private decimal _profit_margin;


public decimal SellingPriceBsS => (decimal)Math.Round(CostPrice * (1 + (ProfitMargin / 100)) * ExchangeRate);
```

Correct: Asynchronous method with CancellationToken and Debounce

```csharp
private CancellationTokenSource? _searchCts;

partial void OnSearchTextChanged(string value)
{
    _searchCts?.Cancel();
    _searchCts = new CancellationTokenSource();
    IsSearching = true;

    // Llamada sin "await" en el cambio de propiedad, delegada a una tarea segura
    _ = ExecuteSearchAsync(value, _searchCts.Token);
}
```
