using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Desktop.Client.ViewModels;

public partial class PendingPickupsViewModel : ObservableObject
{
    private readonly ISalesService _salesService;
    private readonly IDialogService _dialogService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string? _successMessage;

    public ObservableCollection<PendingPickupClientDto> Pickups { get; } = new();

    public IEnumerable<PendingPickupClientDto> FilteredPickups
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SearchQuery)) return Pickups;
            var q = SearchQuery.Trim().ToLower();
            return Pickups.Where(p =>
                (p.CustomerName ?? string.Empty).ToLower().Contains(q) ||
                (p.CustomerCedula ?? string.Empty).ToLower().Contains(q) ||
                (p.InvoiceNumber?.ToString() ?? p.SaleId.ToString()).Contains(q));
        }
    }

    partial void OnSearchQueryChanged(string value) => OnPropertyChanged(nameof(FilteredPickups));

    public PendingPickupsViewModel(ISalesService salesService, IDialogService dialogService)
    {
        _salesService = salesService;
        _dialogService = dialogService;
        Pickups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(FilteredPickups));
    }

    public async Task EnsureLoadedAsync()
    {
        IsLoading = true;
        SuccessMessage = null;
        try
        {
            var list = await _salesService.GetPendingPickupsAsync();
            Pickups.Clear();
            foreach (var item in list.OrderByDescending(p => p.Date))
                Pickups.Add(item);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al cargar retiros pendientes: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await EnsureLoadedAsync();

    [RelayCommand]
    private async Task ConfirmPickupAsync(PendingPickupClientDto? pickup)
    {
        if (pickup == null) return;

        string invoiceLabel = pickup.InvoiceNumber.HasValue
            ? $"Factura N° {pickup.InvoiceNumber:D5}"
            : $"Pedido #{pickup.SaleId}";

        bool confirmed = _dialogService.ShowConfirm(
            "Confirmar Entrega",
            $"¿Confirmar la entrega de mercancía a {pickup.CustomerName}?\n{invoiceLabel}\nTotal: ${pickup.TotalUSD:N2} USD");

        if (!confirmed) return;

        IsLoading = true;
        try
        {
            await _salesService.ConfirmPickupAsync(pickup.SaleId);
            SuccessMessage = $"¡Retiro confirmado! {invoiceLabel} entregado a {pickup.CustomerName}.";
            await EnsureLoadedAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al confirmar retiro: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
