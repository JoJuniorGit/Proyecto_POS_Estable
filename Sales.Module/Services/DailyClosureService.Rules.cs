using Core.Helpers;
using Sales.Module.Entities;
using Sales.Module.Interfaces;

namespace Sales.Module.Services;

public partial class DailyClosureService
{
    private static ShiftReportDetailResult BuildReportDetail(
        int paymentMethodId,
        string methodName,
        string currency,
        decimal declaredNative,
        decimal expectedBsS,
        decimal rate)
    {
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
        var duplicatedMethodIds = declarations
            .GroupBy(d => d.PaymentMethodId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicatedMethodIds.Count > 0)
        {
            throw new ArgumentException(
                $"El desglose contiene métodos de pago duplicados: {string.Join(", ", duplicatedMethodIds)}.",
                nameof(declarations));
        }

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
            // 8.211-T3b2 (S8): la moneda persistida del método es la autoridad; el nombre solo se
            // usa cuando no hay moneda (fila legacy). La misma moneda gobierna la conversión y el
            // renglón del reporte para que nunca se clasifiquen distinto.
            string currency = string.IsNullOrWhiteSpace(expected.PaymentMethodCurrency)
                ? PaymentMethodCurrencyResolver.Resolve(expected.PaymentMethodName)
                : expected.PaymentMethodCurrency;

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
                currency,
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
                bool isCash = methodEntity?.IsCash ?? false;

                // SEC-12: un método electrónico ACTIVO con movimientos no se auto-aprueba. El
                // cajero debe declarar su arqueo; de lo contrario el cierre mostraría una
                // diferencia cero sobre un monto que nunca fue contado. Un método desactivado
                // no puede declararse desde la UI: se incluye con contado 0 para exponer la
                // diferencia real sin bloquear el cierre.
                bool requiresExplicitCount = methodEntity is { IsActive: true }
                    && !isCash
                    && exp.ExpectedAmountBsS > 0m;
                if (requiresExplicitCount)
                {
                    throw new ArgumentException(
                        $"Debe declarar el monto del método electrónico '{exp.PaymentMethodName}' (esperado {exp.ExpectedAmountBsS:0.00} Bs.S) antes de cerrar.",
                        nameof(expectedTotals));
                }

                decimal actualAmount = 0m;

                details.Add(new ClosureDetail
                {
                    PaymentMethodId = exp.PaymentMethodId,
                    PaymentMethodName = exp.PaymentMethodName,
                    ExpectedAmountBsS = exp.ExpectedAmountBsS,
                    ActualAmountBsS = actualAmount,
                    DifferenceBsS = actualAmount - exp.ExpectedAmountBsS
                });

                // 8.211-T3b2 (S8): autoridad en cascada para métodos sin declaración explícita:
                // moneda persistida del método cargado -> moneda del esperado -> nombre (legacy).
                string currency = methodEntity is not null
                    ? PaymentMethodCurrencyResolver.ResolveStoredOrName(methodEntity.Currency, methodEntity.Name)
                    : (string.IsNullOrWhiteSpace(exp.PaymentMethodCurrency)
                        ? PaymentMethodCurrencyResolver.Resolve(exp.PaymentMethodName)
                        : exp.PaymentMethodCurrency);

                decimal declaredNative = currency == PaymentMethodCurrencyResolver.Usd
                    ? PricingCalculator.ToUSD(actualAmount, exchangeRate)
                    : actualAmount;

                reportDetails.Add(BuildReportDetail(
                    exp.PaymentMethodId,
                    exp.PaymentMethodName,
                    currency,
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
