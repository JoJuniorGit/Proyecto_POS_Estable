using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Desktop.Client.ViewModels;

/// <summary>
/// Representa una tarjeta reactiva en el historial de los últimos 3 productos escaneados.
/// Su propiedad Quantity NO es un contador aislado, sino que está enlazada directamente
/// a la cantidad real en la línea de detalle correspondiente de la orden activa (CartViewModel).
/// </summary>
public partial class RecentScannedItemViewModel : ObservableObject
{
    private readonly CartViewModel _cart;
    private readonly Func<int, decimal, Task> _onUpdateQuantity;

    public int ProductId { get; }
    public string SKU { get; }
    public string ProductName { get; }
    public decimal UnitPriceBsS { get; }
    public decimal UnitPriceUSD { get; }

    public decimal Quantity => _cart.CartItems.FirstOrDefault(i => i.Model.ProductId == ProductId)?.Model.Quantity ?? 0m;

    public string TotalQuantityText => Quantity % 1m == 0m ? $"x{Quantity:0}" : $"x{Quantity:0.000}";

    public RecentScannedItemViewModel(
        int productId, 
        string sku, 
        string productName, 
        decimal unitPriceBsS, 
        decimal unitPriceUSD, 
        CartViewModel cart, 
        Func<int, decimal, Task> onUpdateQuantity)
    {
        ProductId = productId;
        SKU = sku ?? string.Empty;
        ProductName = productName ?? string.Empty;
        UnitPriceBsS = unitPriceBsS;
        UnitPriceUSD = unitPriceUSD;
        _cart = cart ?? throw new ArgumentNullException(nameof(cart));
        _onUpdateQuantity = onUpdateQuantity ?? throw new ArgumentNullException(nameof(onUpdateQuantity));
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Quantity));
        OnPropertyChanged(nameof(TotalQuantityText));
    }

    [RelayCommand]
    private async Task IncrementQuantityAsync()
    {
        var lineItem = _cart.CartItems.FirstOrDefault(i => i.Model.ProductId == ProductId);
        if (lineItem != null)
        {
            await _onUpdateQuantity(lineItem.Id, lineItem.Model.Quantity + 1m);
            Refresh();
        }
    }

    [RelayCommand]
    private async Task DecrementQuantityAsync()
    {
        var lineItem = _cart.CartItems.FirstOrDefault(i => i.Model.ProductId == ProductId);
        if (lineItem != null && lineItem.Model.Quantity > 0m)
        {
            await _onUpdateQuantity(lineItem.Id, Math.Max(0m, lineItem.Model.Quantity - 1m));
            Refresh();
        }
    }
}
