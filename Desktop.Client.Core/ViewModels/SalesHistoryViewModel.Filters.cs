using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

public partial class SalesHistoryViewModel
{
    private readonly HashSet<string> _knownCashiers = new(StringComparer.OrdinalIgnoreCase);
    public const string TestCashierName = "BOT_STRESS_TEST";

    private List<SaleHistoryDto> _pageBuffer = new();

    private ObservableCollection<string> _cashiers = new();
    public ObservableCollection<string> Cashiers
    {
        get => _cashiers;
        private set => SetProperty(ref _cashiers, value);
    }

    private string _cashierFilterText = string.Empty;
    public string CashierFilterText
    {
        get => _cashierFilterText;
        set
        {
            if (SetProperty(ref _cashierFilterText, value ?? string.Empty))
            {
                ApplyCurrentPageFilter();
            }
        }
    }

    private bool _hideTestTransactions;
    public bool HideTestTransactions
    {
        get => _hideTestTransactions;
        set
        {
            if (SetProperty(ref _hideTestTransactions, value))
            {
                ApplyCurrentPageFilter();
            }
        }
    }

    private int _hiddenByFilterCount;
    public int HiddenByFilterCount
    {
        get => _hiddenByFilterCount;
        private set
        {
            if (SetProperty(ref _hiddenByFilterCount, value))
            {
                OnPropertyChanged(nameof(HiddenByFilterSummary));
            }
        }
    }

    public string HiddenByFilterSummary
    {
        get
        {
            return HiddenByFilterCount > 0
                ? $"Ocultas por el filtro: {HiddenByFilterCount}"
                : "Todas las ventas de la página se muestran";
        }
    }

    private bool IsSaleVisible(SaleHistoryDto sale)
    {
        if (_hideTestTransactions && string.Equals(sale.CashierName, TestCashierName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string term = _cashierFilterText.Trim();
        if (term.Length > 0 && sale.CashierName.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return true;
    }

    private void ApplyCurrentPageFilter()
    {
        var visible = _pageBuffer.Where(IsSaleVisible).ToList();
        ReplaceCollection(Sales, visible);
        TotalBsSForThePeriod = visible.Sum(s => s.FinalPaidAmountBsS);
        HiddenByFilterCount = _pageBuffer.Count - visible.Count;
    }
}