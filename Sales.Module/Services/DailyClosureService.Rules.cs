using Core.Helpers;
using Sales.Module.Entities;
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

    private static void ValidateDeclaredMethods(
        IReadOnlyList<DeclaredPaymentAmount> declarations,
        Dictionary<int, ExpectedTotalDto> expectedById)
    {
        var unknownMethodIds = declarations
            .Where(d => !expectedById.ContainsKey(d.PaymentMethodId))
            .Select(d => d.PaymentMethodId)
            .Distinct()
            .ToList();

        if (unknownMethodIds.Count > 0)
        {
            throw new ArgumentException(
                $"El desglose contiene métodos de pago no reconocidos: {string.Join(", ", unknownMethodIds)}.",
                nameof(declarations));
        }
    }

    private static void BuildDeclaredDetails(
        IReadOnlyList<DeclaredPaymentAmount> declarations,
        Dictionary<int, ExpectedTotalDto> expectedById,
        decimal exchangeRate,
        List<ClosureDetail> details,
        List<ShiftReportDetailResult> reportDetails)
    {
        foreach (var declared in declarations)
        {
            var expected = expectedById[declared.PaymentMethodId];
            string currency = PaymentMethodCurrencyResolver.Resolve(expected.PaymentMethodName);

            decimal actualAmountBsS = currency == PaymentMethodCurrencyResolver.Usd
                ? declared.Amount * exchangeRate
                : declared.Amount;
            decimal expectedAmountBsS = expected.ExpectedAmountBsS;

            details.Add(new ClosureDetail
            {
                PaymentMethodId = declared.PaymentMethodId,
                PaymentMethodName = expected.PaymentMethodName,
                ExpectedAmountBsS = expectedAmountBsS,
                ActualAmountBsS = actualAmountBsS,
                DifferenceBsS = actualAmountBsS - expectedAmountBsS
            });

            reportDetails.Add(BuildReportDetail(
                declared.PaymentMethodId,
                expected.PaymentMethodName,
                declared.Amount,
                expectedAmountBsS,
                exchangeRate));
        }
    }

    private static void MergeMissingMethodsWithReport(
        List<ClosureDetail> details,
        List<ShiftReportDetailResult> reportDetails,
        List<ExpectedTotalDto> expectedTotals,
        HashSet<int> existingMethodIds,
        Dictionary<int, PaymentMethod> methodEntities,
        decimal exchangeRate)
    {
        foreach (var exp in expectedTotals)
        {
            if (!existingMethodIds.Contains(exp.PaymentMethodId))
            {
                methodEntities.TryGetValue(exp.PaymentMethodId, out var methodEntity);
                decimal actualAmount = (methodEntity != null && methodEntity.IsCash) ? 0m : exp.ExpectedAmountBsS;

                details.Add(new ClosureDetail
                {
                    PaymentMethodId = exp.PaymentMethodId,
                    PaymentMethodName = exp.PaymentMethodName,
                    ExpectedAmountBsS = exp.ExpectedAmountBsS,
                    ActualAmountBsS = actualAmount,
                    DifferenceBsS = actualAmount - exp.ExpectedAmountBsS
                });

                string currency = PaymentMethodCurrencyResolver.Resolve(exp.PaymentMethodName);
                decimal declaredNative = currency == PaymentMethodCurrencyResolver.Usd
                    ? PricingCalculator.ToUSD(actualAmount, exchangeRate)
                    : actualAmount;

                reportDetails.Add(BuildReportDetail(
                    exp.PaymentMethodId,
                    exp.PaymentMethodName,
                    declaredNative,
                    exp.ExpectedAmountBsS,
                    exchangeRate));
            }
        }
    }

    private static void RecalculateTotals(DailyClosure closure)
    {
        foreach (var detail in closure.Details)
        {
            if (detail.ActualAmountBsS < 0)
            {
                throw new ArgumentException(
                    $"El monto declarado para '{detail.PaymentMethodName}' no puede ser negativo.",
                    nameof(closure));
            }
            detail.DifferenceBsS = detail.ActualAmountBsS - detail.ExpectedAmountBsS;
        }

        closure.TotalExpectedBsS = closure.Details.Sum(d => d.ExpectedAmountBsS);
        closure.TotalActualBsS = closure.Details.Sum(d => d.ActualAmountBsS);
        closure.TotalDifferenceBsS = closure.TotalActualBsS - closure.TotalExpectedBsS;
    }
}
