using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Desktop.Client.ViewModels;

/// <summary>
/// F4-WU1 (Paso 14): proyección de los desgloses del backend a filas y barras nativas WPF.
/// No se usan librerías de gráficos: cada fila expone el ancho ya calculado y la vista lo dibuja
/// con primitivas (Rectangle/ItemsControl).
/// </summary>
public partial class ProfitabilityViewModel
{
    private const double MaxBarWidth = 160d;

    private void RebuildBreakdown()
    {
        RebuildSeriesBars();
        RebuildPaymentMethods();
        RebuildProducts();
        RebuildCategories();
        RebuildSellers();
        RebuildAlerts();
    }

    private void RebuildSeriesBars()
    {
        SeriesBars.Clear();
        var points = _summary?.Breakdown.Series ?? Array.Empty<ProfitabilitySeriesPointClientDto>();
        var max = points.Count == 0
            ? 0m
            : points.Max(point => Math.Max(Math.Abs(point.AccruedNetProfit), Math.Abs(point.RealizedNetProfit)));

        var showAccrued = ViewMode is ProfitabilityViewMode.Accrued or ProfitabilityViewMode.Both;
        var showRealized = ViewMode is ProfitabilityViewMode.Realized or ProfitabilityViewMode.Both;

        foreach (var point in points)
        {
            SeriesBars.Add(new ProfitabilitySeriesBarViewModel
            {
                Label = point.Label,
                AccruedNetProfit = point.AccruedNetProfit,
                RealizedNetProfit = point.RealizedNetProfit,
                AccruedDisplay = FormatMoney(point.AccruedNetProfit),
                RealizedDisplay = FormatMoney(point.RealizedNetProfit),
                AccruedBarWidth = ScaleWidth(point.AccruedNetProfit, max),
                RealizedBarWidth = ScaleWidth(point.RealizedNetProfit, max),
                AccruedIsNegative = point.AccruedNetProfit < 0m,
                RealizedIsNegative = point.RealizedNetProfit < 0m,
                ShowAccrued = showAccrued,
                ShowRealized = showRealized
            });
        }
    }

    private void RebuildPaymentMethods()
    {
        PaymentMethodRows.Clear();
        var methods = _summary?.Breakdown.PaymentMethods ?? Array.Empty<PaymentMethodBreakdownClientDto>();
        var max = methods.Count == 0
            ? 0m
            : methods.Max(method => Math.Abs(method.CollectedAmount));

        foreach (var method in methods)
        {
            PaymentMethodRows.Add(new ProfitabilityPaymentMethodRowViewModel
            {
                Name = method.PaymentMethodName,
                CollectedDisplay = FormatMoney(method.CollectedAmount),
                FeeDisplay = FormatMoney(method.FeeAmount),
                CoverageDisplay = FormatPercent(method.FeeCoveragePercent),
                BarWidth = ScaleWidth(method.CollectedAmount, max),
                CollectedAmount = method.CollectedAmount,
                FeeAmount = method.FeeAmount
            });
        }
    }

    private void RebuildProducts()
    {
        ProductRows.Clear();
        var products = _summary?.Breakdown.Products ?? Array.Empty<ProductBreakdownClientDto>();
        var max = products.Count == 0
            ? 0m
            : products.Max(product => Math.Abs(product.Revenue));

        foreach (var product in products)
        {
            ProductRows.Add(new ProfitabilityProductRowViewModel
            {
                Name = product.ProductName,
                CategoryName = product.CategoryName ?? "Sin categoría",
                QuantityDisplay = CurrencyDisplay.Number(product.QuantitySold, 2),
                RevenueDisplay = FormatMoney(product.Revenue),
                GrossProfitDisplay = FormatMoney(product.GrossProfit),
                MarginDisplay = FormatPercent(product.GrossMarginPercent),
                BarWidth = ScaleWidth(product.Revenue, max),
                Revenue = product.Revenue,
                GrossProfit = product.GrossProfit
            });
        }
    }

    private void RebuildCategories()
    {
        CategoryRows.Clear();
        var categories = _summary?.Breakdown.Categories ?? Array.Empty<CategoryBreakdownClientDto>();
        var max = categories.Count == 0
            ? 0m
            : categories.Max(category => Math.Abs(category.Revenue));

        foreach (var category in categories)
        {
            CategoryRows.Add(new ProfitabilityCategoryRowViewModel
            {
                Name = category.CategoryName,
                RevenueDisplay = FormatMoney(category.Revenue),
                GrossProfitDisplay = FormatMoney(category.GrossProfit),
                MarginDisplay = FormatPercent(category.GrossMarginPercent),
                ContributionDisplay = FormatPercent(category.ContributionPercent),
                BarWidth = ScaleWidth(category.Revenue, max),
                Revenue = category.Revenue,
                GrossProfit = category.GrossProfit
            });
        }
    }

    private void RebuildSellers()
    {
        SellerRows.Clear();
        foreach (var seller in _summary?.Breakdown.Sellers ?? Array.Empty<SellerBreakdownClientDto>())
        {
            SellerRows.Add(new ProfitabilitySellerRowViewModel
            {
                Name = string.IsNullOrWhiteSpace(seller.CashierName) ? "Sin cajero" : seller.CashierName,
                NetSalesDisplay = FormatMoney(seller.NetSales),
                GrossProfitDisplay = FormatMoney(seller.GrossProfit)
            });
        }
    }

    private void RebuildAlerts()
    {
        Alerts.Clear();
        var alerts = _summary?.Alerts is { Count: > 0 } summaryAlerts
            ? summaryAlerts
            : _summary?.Breakdown.Alerts ?? Array.Empty<ProfitabilityAlertClientDto>();

        foreach (var alert in alerts)
        {
            Alerts.Add(new ProfitabilityAlertRowViewModel
            {
                Message = alert.Message,
                SeverityLabel = alert.Severity switch
                {
                    ProfitabilityAlertSeverityClient.Critical => "Crítico",
                    ProfitabilityAlertSeverityClient.Warning => "Advertencia",
                    _ => "Información"
                },
                SeverityColor = alert.Severity switch
                {
                    ProfitabilityAlertSeverityClient.Critical => "#DC2626",
                    ProfitabilityAlertSeverityClient.Warning => "#B45309",
                    _ => "#2563EB"
                },
                DetailDisplay = $"{FormatPercent(alert.CurrentValue)} vs. {FormatPercent(alert.PreviousValue)} (Δ {FormatPercent(alert.Delta)})"
            });
        }
    }

    private static double ScaleWidth(decimal value, decimal max)
    {
        if (max <= 0m)
        {
            return 0d;
        }

        var ratio = Math.Abs(value) / max;
        return Math.Round((double)ratio * MaxBarWidth, 2);
    }
}
