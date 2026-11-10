using System;
using Core.Helpers;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

/// <summary>
/// T4 (Paso 13 / 8.188, S1/S2/S5): fila del listado de caja chica. Solo presenta los snapshots
/// congelados del gasto; no recalcula montos ni políticas.
/// </summary>
public sealed class PettyCashExpenseRowViewModel
{
    public PettyCashExpenseRowViewModel(PettyCashExpenseClientDto expense)
    {
        Expense = expense ?? throw new ArgumentNullException(nameof(expense));
    }

    public PettyCashExpenseClientDto Expense { get; }

    public string VoucherCode => Expense.VoucherCode;

    public string CreatedAtDisplay
    {
        get
        {
            var local = TimeZoneHelper.ToVenezuelaTime(Expense.CreatedAtUtc);
            return local.ToString("dd/MM/yyyy HH:mm");
        }
    }

    public string CategoryLabel => PettyCashLabels.Category(Expense.Category);

    public string CurrencyLabel => PettyCashLabels.Currency(Expense.Currency);

    public string ConceptDisplay => Expense.Concept;

    public string BeneficiaryDisplay => Expense.Beneficiary;

    public string ReferenceDisplay => Expense.ReferenceNumber;

    public string AmountBsSDisplay => CurrencyDisplay.FormatSecondary(Expense.AmountBsS);

    public string AmountUsdDisplay => CurrencyDisplay.FormatPrimary(Expense.AmountUsd);

    public string StatusLabel => PettyCashLabels.Status(Expense.Status);

    public string StatusColor => Expense.Status == PettyCashStatusClient.Cancelled ? "#B91C1C" : "#15803D";

    public string AuthorizerDisplay => Expense.AuthorizedByUserId is int authorizedBy
        ? $"Usuario #{authorizedBy}"
        : "—";

    public bool CanCancel => Expense.Status == PettyCashStatusClient.Active;
}

