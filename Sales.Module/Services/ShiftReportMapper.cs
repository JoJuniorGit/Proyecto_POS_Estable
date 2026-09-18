using Core.Helpers;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using System.Collections.Generic;
using System.Linq;

namespace Sales.Module.Services;

public static class ShiftReportMapper
{
    public static DailyClosureResponseDto MapClosure(DailyClosure closure)
    {
        return new DailyClosureResponseDto(
            closure.Id,
            closure.ClosureDate,
            closure.UserId,
            closure.ExchangeRate,
            closure.TotalExpectedBsS,
            closure.TotalActualBsS,
            closure.TotalDifferenceBsS,
            closure.Observation,
            closure.Details.Select(MapDetail).ToList());
    }

    private static ClosureDetailResponseDto MapDetail(ClosureDetail detail)
    {
        return new ClosureDetailResponseDto(
            detail.Id,
            detail.DailyClosureId,
            detail.PaymentMethodId,
            detail.PaymentMethodName,
            detail.ExpectedAmountBsS,
            detail.ActualAmountBsS,
            detail.DifferenceBsS);
    }

    public static List<ShiftReportDetailDto> MapDetails(
        IReadOnlyList<ClosureDetailResponseDto> details,
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
