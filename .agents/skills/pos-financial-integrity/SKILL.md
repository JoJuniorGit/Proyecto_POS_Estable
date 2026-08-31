---
name: pos-financial-integrity
description: >-
  Enforces financial arithmetic rules, dual-currency conversion (USD reference to Bs.S BCV rate),
  the Tax-Free Cost+Margin pricing model, sales history snapshot immutability, mixed-currency checkout,
  cross-currency change/vuelto calculation, fiscal rounding, cash drawer balancing, and cash advance
  commission integrity. Activate this skill whenever implementing or modifying pricing, discounts,
  payment methods, cash register operations, financial DTOs, or monetary test suites.
---

# POS Financial Integrity & Dual-Currency Engine Guide

This skill governs all monetary, dual-currency, and cash drawer calculations across the POS system to guarantee zero accounting drift and full compliance with system rules.

---

## 1. Tax-Free Pricing Model (Cost + Margin Formula)

* **Strict Rule**: The system is 100% Tax-Free (no VAT / Sin IVA). Prices are calculated exclusively from product cost, profit margin percentage, and active BCV exchange rate.

### Concrete C# Implementation: `PricingHelper.cs`
```csharp
namespace Core.Helpers;

public static class PricingHelper
{
    /// <summary>
    /// Calcula el precio base de venta en USD según el costo y el margen de ganancia: Cost * (1 + (Margin / 100)).
    /// </summary>
    public static decimal CalculatePriceUSD(decimal costUSD, decimal profitMarginPercentage)
    {
        if (costUSD < 0) throw new ArgumentException("El costo no puede ser negativo.", nameof(costUSD));
        
        decimal multiplier = 1m + (profitMarginPercentage / 100m);
        return Math.Round(costUSD * multiplier, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Convierte un monto en USD a Bolívares (Bs.S) usando la tasa oficial BCV con redondeo fiscal a 2 decimales.
    /// </summary>
    public static decimal ToBsS(decimal amountUSD, decimal exchangeRate)
    {
        if (exchangeRate <= 0) return 0m;
        return Math.Round(amountUSD * exchangeRate, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Convierte un monto en Bolívares (Bs.S) a USD equivalente según la tasa oficial BCV con precisión de 4 decimales.
    /// </summary>
    public static decimal ToUSD(decimal amountBsS, decimal exchangeRate)
    {
        if (exchangeRate <= 0) return 0m;
        return Math.Round(amountBsS / exchangeRate, 4, MidpointRounding.AwayFromZero);
    }
}
```

---

## 2. Cross-Currency Change (Vuelto Cruzado)

When a customer pays in USD physical cash (e.g. \$20.00 USD bill) for a \$14.50 USD transaction and the change must be returned in local currency (Bs.S) at BCV rate 36.50:

### Concrete C# Implementation: `PaymentCalculator.cs`
```csharp
namespace Core.Helpers;

public record PaymentChangeResult(
    decimal TotalPaidUsdEquivalent,
    decimal TotalPaidBsSEquivalent,
    decimal ChangeDueUSD,
    decimal ChangeDueBsS,
    bool IsFullyPaid
);

public static class PaymentCalculator
{
    public static PaymentChangeResult CalculateCrossCurrencyChange(
        decimal totalAmountUSD, 
        decimal paidUSD, 
        decimal paidBsS, 
        decimal bcvRate)
    {
        if (bcvRate <= 0) throw new ArgumentException("La tasa BCV debe ser mayor a cero.", nameof(bcvRate));

        // 1. Convert local currency payment to USD equivalent
        decimal paidBsSInUsd = Math.Round(paidBsS / bcvRate, 4, MidpointRounding.AwayFromZero);
        decimal totalPaidUsd = Math.Round(paidUSD + paidBsSInUsd, 4, MidpointRounding.AwayFromZero);

        // 2. Check if balance is satisfied
        if (totalPaidUsd < totalAmountUSD)
        {
            return new PaymentChangeResult(
                TotalPaidUsdEquivalent: totalPaidUsd,
                TotalPaidBsSEquivalent: Math.Round(totalPaidUsd * bcvRate, 2, MidpointRounding.AwayFromZero),
                ChangeDueUSD: 0m,
                ChangeDueBsS: 0m,
                IsFullyPaid: false
            );
        }

        // 3. Compute excess change in USD and converted to Bs.S
        decimal changeUSD = Math.Round(totalPaidUsd - totalAmountUSD, 2, MidpointRounding.AwayFromZero);
        decimal changeBsS = Math.Round(changeUSD * bcvRate, 2, MidpointRounding.AwayFromZero);

        return new PaymentChangeResult(
            TotalPaidUsdEquivalent: totalPaidUsd,
            TotalPaidBsSEquivalent: Math.Round(totalPaidUsd * bcvRate, 2, MidpointRounding.AwayFromZero),
            ChangeDueUSD: changeUSD,
            ChangeDueBsS: changeBsS,
            IsFullyPaid: true
        );
    }
}
```

---

## 3. Stock Deductions & `DeliverOnCredit` Protection

* **Guard Against Double Stock Deduction**: In credit sales, orders on hold, or advance deliveries (`DeliverOnCredit`), if the physical items were already marked as delivered (`IsDelivered == true`), the final payment/checkout MUST NOT deduct inventory a second time.

```csharp
public async Task CompleteSaleTransactionAsync(Sale sale, CancellationToken cancellationToken)
{
    // 1. Sanitize: Eliminate zero or negative quantity line items
    sale.Items.RemoveAll(item => item.Quantity <= 0);

    if (!sale.Items.Any())
        throw new InvalidOperationException("No se puede completar una venta sin ítems válidos.");

    // 2. Deduct inventory ONLY for items not previously delivered
    foreach (var item in sale.Items)
    {
        if (!item.IsDelivered)
        {
            await _stockService.DeductStockAsync(item.ProductId, item.Quantity, cancellationToken);
            item.IsDelivered = true; // Mark as delivered once deducted
        }
    }

    // 3. Complete sale status and freeze snapshot
    sale.Status = SaleStatus.Completed;
    await _salesRepository.SaveChangesAsync(cancellationToken);
}
```

---

## 4. Daily Closure (Cierre Z) Reconciliation Formula

The daily closure reconciles physical drawer contents with recorded sales without destroying transaction history:

$$\text{Expected Physical Cash (Bs.S)} = \text{Opening Balance} + \sum \text{Cash Sales (Bs.S)} - \sum \text{Cash Advances} - \sum \text{Cash Withdrawals}$$
$$\text{Expected Electronic Balance} = \sum \text{Pago Móvil} + \sum \text{Punto de Venta} + \sum \text{Transferencias} + \sum \text{Advance Electronic Incomes}$$
$$\text{Cash Discrepancy (Diferencia)} = \text{Counted Physical Cash} - \text{Expected Physical Cash}$$

---

## 5. Immutability of Sales History (Snapshot Pattern)

* **Rule**: Recalculating historical sales totals using the current `ExchangeRateService` rate is **strictly PROHIBITED**.
* All amounts, exchange rates, and cash adjustments are frozen upon checkout into persistent snapshot columns:

```csharp
public class Sale
{
    public int Id { get; set; }
    public decimal AppliedRate { get; set; }        // Frozen exchange rate at checkout moment
    public decimal TotalUSD { get; set; }           // Authoritative original USD total
    public decimal TotalBsS { get; set; }           // System-calculated Bs.S total (digital)
    public decimal FinalPaidAmountBsS { get; set; } // Actual physical/digital Bs.S collected
    public decimal RoundingAdjustment { get; set; } // Physical cash rounding adjustment
    public DateTime CreatedAtUtc { get; set; }
}
```

---

## 6. Self-Evaluation Test Suite

Run monetary and pricing tests:
```powershell
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~Payment|FullyQualifiedName~Pricing|FullyQualifiedName~Financial"
```
