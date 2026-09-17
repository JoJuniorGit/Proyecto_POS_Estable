using Core.Helpers;
using Sales.Module.Entities;
using System.Collections.Generic;
using System.Linq;

namespace Sales.Module.Services;

public static class ShiftReportMapper
{
    public static List<ShiftReportDetailDto> MapDetails(
        IReadOnlyList<ClosureDetail> details,
        decimal exchangeRate)
    {
        return details.Select(d =>
        {
            string currency = PaymentMethodCurrencyResolver.Resolve(d.PaymentMethodName);
            bool isUsd = currency == PaymentMethodCurrencyResolver.Usd;
            decimal systemAmt = isUsd
                ? PricingCalculator.ToUSD(d.ExpectedAmountBsS, exchangeRate)
                : d.ExpectedAmountBsS;
            decimal declaredAmt = isUsd
                ? PricingCalculator.ToUSD(d.ActualAmountBsS, exchangeRate)
                : d.ActualAmountBsS;
            decimal diff = declaredAmt - systemAmt;
            string status = Math.Abs(diff) < 0.05m
                ? "Balanced"
                : (diff > 0 ? "Surplus" : "Shortage");

            return new ShiftReportDetailDto
            {
                PaymentMethodId = d.PaymentMethodId,
                PaymentMethodName = d.PaymentMethodName,
                Currency = currency,
                DeclaredAmount = declaredAmt,
                SystemAmount = systemAmt,
                Difference = diff,
                Status = status
            };
        }).ToList();
    }
}
