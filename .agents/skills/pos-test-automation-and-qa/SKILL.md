---
name: pos-test-automation-and-qa
description: >-
  Standardizes automated testing patterns across backend services, API controllers, and WPF ViewModels.
  Covers integration testing with WebApplicationFactory, fluent Test Data Builders (SaleBuilder, ProductBuilder),
  mocking strategies for multi-currency services, and non-blocking ViewModel testing. Activate this skill
  when writing unit tests, adding integration test suites, increasing code coverage (>70%), or resolving test failures.
---

# POS Test Automation & Quality Assurance Guide

This skill governs testing standards across the POS system to achieve **>70% test coverage** on critical services while ensuring fast, reliable, non-flaky test execution.

---

## 1. Fluent Test Data Builders Pattern

To keep tests clean, readable, and resilient against model changes, use fluent builders:

### A. `ProductBuilder.cs`
```csharp
namespace CommandCenter.Tests.Builders;

public class ProductBuilder
{
    private int _id = 1;
    private string _sku = "PROD-001";
    private string _name = "Test Product";
    private decimal _costPriceUsd = 10.00m;
    private decimal _profitMargin = 30.00m;
    private decimal _stock = 100m;
    private bool _isActive = true;

    public ProductBuilder WithStock(decimal stock) { _stock = stock; return this; }
    public ProductBuilder WithCostAndMargin(decimal cost, decimal margin) { _costPriceUsd = cost; _profitMargin = margin; return this; }
    public ProductBuilder AsInactive() { _isActive = false; return this; }

    public Product Build() => new Product
    {
        Id = _id,
        SKU = _sku,
        Name = _name,
        CostPriceUSD = _costPriceUsd,
        ProfitMargin = _profitMargin,
        PriceUSD = Math.Round(_costPriceUsd * (1 + (_profitMargin / 100m)), 2, MidpointRounding.AwayFromZero),
        StockQuantity = _stock,
        IsActive = _isActive
    };
}
```

---

## 2. Integration Testing with `WebApplicationFactory`

Test end-to-end HTTP API endpoints in memory without external server dependencies:

```csharp
namespace CommandCenter.Tests.Integration;

public class SalesApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public SalesApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace external dependencies with in-memory or mocks if needed
            });
        }).CreateClient();
    }

    [Fact]
    public async Task CompleteSale_Returns200_AndDeductsStock()
    {
        var checkoutPayload = new CheckoutRequestDto
        {
            CustomerId = 1,
            Items = new[] { new CartItemDto { ProductId = 1, Quantity = 2 } },
            Payments = new[] { new PaymentDto { MethodCode = "USD_CASH", Amount = 26.00m } }
        };

        var response = await _client.PostAsJsonAsync("/api/v1/sales/checkout", checkoutPayload);
        
        response.EnsureSuccessStatusCode();
        var saleResult = await response.Content.ReadFromJsonAsync<SaleResultDto>();
        Assert.NotNull(saleResult);
        Assert.Equal(SaleStatus.Completed, saleResult.Status);
    }
}
```

---

## 3. Safe ViewModel Unit Testing (Dispatcher Mocking)

**Anti-Pattern**: Calling ViewModels in unit tests that execute `Application.Current.Dispatcher.Invoke` causes `NullReferenceException` in headless test runners.

**Best Practice**: Ensure ViewModels use null-safe dispatcher calls `Application.Current?.Dispatcher?.BeginInvoke(...)` and mock `IDialogService`:

```csharp
[Fact]
public void PosViewModel_ClearCart_ExecutesSafelyWithoutUIThread()
{
    // Arrange
    var mockProductService = new Mock<IProductService>();
    var mockDialogService = new Mock<IDialogService>();
    var userSession = new UserSession();

    var vm = new PosViewModel(mockProductService.Object, mockDialogService.Object, userSession);

    // Act
    vm.ClearCartCommand.Execute(null);

    // Assert
    Assert.Empty(vm.CartItems);
}
```

---

## 4. Test Execution & Coverage Recipes

Run specific test suites with high performance:

```powershell
# Run entire test suite
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --no-build

# Run only financial and pricing tests
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~Financial|FullyQualifiedName~Pricing"

# Run only security and authentication tests
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~Security|FullyQualifiedName~Auth"
```
