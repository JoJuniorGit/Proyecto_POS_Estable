---
name: pos-security-hardening
description: >-
  Audits and enforces security guardrails across the POS system. Covers local network pairing security
  (/api/pairing/info protection, ephemeral QR tokens), Zero-Trust backend price and discount validation,
  Role-Based Access Control (RBAC: Admin vs Cashier separation), JWT validation, CORS, and sanitization.
  Activate this skill when creating or modifying API endpoints, authentication, QR pairing workflows,
  cashier privilege checks, or when performing security code reviews.
---

# POS Security Hardening & Zero-Trust Defense Guide

This skill governs security policies across the POS system, ensuring that clients (Web, Mobile Scanner, Desktop) cannot tamper with financial data, escalate privileges, or expose local network infrastructure.

---

## 1. Zero-Trust Backend Price Validation

**Golden Rule**: The frontend or mobile scanner is completely untrusted for pricing. The backend must always re-evaluate and enforce item prices from the authoritative catalog and active price list (`Retail` or `Wholesale`).

```csharp
namespace Sales.Module.Services;

public async Task<SaleDto> AddItemSecureAsync(
    int saleId, 
    int productId, 
    decimal requestedQuantity, 
    string priceListType, 
    CancellationToken cancellationToken)
{
    var product = await _productRepository.GetByIdAsync(productId, cancellationToken)
        ?? throw new KeyNotFoundException($"Producto ID {productId} no existe.");

    if (!product.IsActive)
        throw new InvalidOperationException("No se pueden vender productos inactivos.");

    // Authoritative pricing according to price list:
    decimal authoritativePriceUsd = priceListType.Equals("Wholesale", StringComparison.OrdinalIgnoreCase) 
        && product.PriceWholesaleUSD > 0 
        ? product.PriceWholesaleUSD 
        : product.PriceUSD;

    var saleItem = new SaleItem
    {
        SaleId = saleId,
        ProductId = productId,
        Quantity = requestedQuantity,
        UnitPriceUSD = authoritativePriceUsd, // Never accept unit price from client payload!
        SubtotalUSD = Math.Round(authoritativePriceUsd * requestedQuantity, 2, MidpointRounding.AwayFromZero)
    };

    return await _salesRepository.AddItemAsync(saleItem, cancellationToken);
}
```

---

## 2. Pairing Endpoint Security (`/api/pairing/info`)

**Rule**: The pairing endpoint reveals sensitive LAN IP and service ports. It must be accessible locally (`localhost` for the desktop client) or authenticated via a pairing token header (`X-Pairing-Token` or JWT) for mobile tablets/scanners.

```csharp
namespace Backend.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PairingController : ControllerBase
{
    private readonly INetworkDiscoveryService _networkDiscoveryService;
    private readonly IPairingTokenService _pairingTokenService;

    public PairingController(INetworkDiscoveryService networkDiscoveryService, IPairingTokenService pairingTokenService)
    {
        _networkDiscoveryService = networkDiscoveryService;
        _pairingTokenService = pairingTokenService;
    }

    [HttpGet("info")]
    public IActionResult GetPairingInfo([FromHeader(Name = "X-Pairing-Token")] string? pairingToken)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        bool isLocal = remoteIp != null && IPAddress.IsLoopback(remoteIp);

        if (!isLocal)
        {
            if (string.IsNullOrWhiteSpace(pairingToken) || !_pairingTokenService.ValidateToken(pairingToken))
            {
                return Unauthorized(new { message = "Acceso denegado: Token de emparejamiento inválido o ausente." });
            }
        }

        var networkInfo = _networkDiscoveryService.GetLocalNetworkInfo();
        return Ok(networkInfo);
    }
}
```

---

## 3. Role-Based Access Control (RBAC) & Controller Audit Checklist

Every controller endpoint must declare explicit authorization attributes:

```csharp
[ApiController]
[Route("api/v1/[controller]")]
[Authorize] // Require authenticated JWT by default
public class SettingsController : ControllerBase
{
    [HttpPut("printers")]
    [Authorize(Roles = "Admin,Manager")] // Restricted mutating actions
    public async Task<IActionResult> UpdatePrinterSettingsAsync([FromBody] PrinterSettingsDto dto)
    {
        ...
    }
}
```

### In-Memory JWT Storage in WPF Desktop
* **Rule**: Tokens must reside strictly inside `UserSession` in memory:
  ```csharp
  public class UserSession
  {
      public string? Token { get; private set; }
      public UserRole Role { get; private set; }
      // NEVER write Token to plain text files (.json, .txt) on disk!
  }
  ```

---

## 4. Product Import Validation Guard

```csharp
public static ValidationResult ValidateImportRecord(ProductImportRow row)
{
    if (string.IsNullOrWhiteSpace(row.SKU)) return ValidationResult.Fail("SKU es obligatorio.");
    if (string.IsNullOrWhiteSpace(row.Name)) return ValidationResult.Fail("Nombre es obligatorio.");
    if (row.CostPriceUSD < 0) return ValidationResult.Fail("El costo no puede ser negativo.");
    if (row.ProfitMargin < 0) return ValidationResult.Fail("El margen no puede ser negativo.");
    return ValidationResult.Success();
}
```

---

## 5. Self-Evaluation Security Test Recipe

Run security and RBAC validation test suites:
```powershell
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~Security|FullyQualifiedName~Auth"
```
