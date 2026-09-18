using Core.Helpers;
using Sales.Module.Interfaces;

namespace Sales.Module.Services;

public partial class DailyClosureService
{
    private static ShiftReportDetailResult BuildReportDetail(
        int paymentMethodId,
        string methodName,
        decimal declaredNative,
        decimal expectedBsS,
        decimal rate)
    {
        string currency = PaymentMethodCurrencyResolver.Resolve(methodName);
        decimal systemAmount = currency == PaymentMethodCurrencyResolver.Usd
            ? PricingCalculator.ToUSD(expectedBsS, rate)
            : expectedBsS;
        decimal difference = declaredNative - systemAmount;
        string status = Math.Abs(difference) < 0.05m
            ? ClosureStatus.Balanced
            : (difference > 0 ? ClosureStatus.Surplus : ClosureStatus.Shortage);

        return new ShiftReportDetailResult(
            paymentMethodId,
            methodName,
            currency,
            declaredNative,
            systemAmount,
            difference,
            status);
    }
}
