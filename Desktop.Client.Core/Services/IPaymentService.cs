using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Desktop.Client.Services;

public partial class PaymentMethodDto : ObservableObject
{
    private int _id;
    public int Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    private bool _isActive = true;
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    private bool _requiresReference = false;
    public bool RequiresReference
    {
        get => _requiresReference;
        set => SetProperty(ref _requiresReference, value);
    }

    private bool _isCash = false;
    public bool IsCash
    {
        get => _isCash;
        set => SetProperty(ref _isCash, value);
    }

    private string _currency = "Bs.S";
    /// <summary>
    /// 8.9-M16: moneda efectiva del método de pago provista por el backend (fuente de verdad).
    /// </summary>
    public string Currency
    {
        get => _currency;
        set => SetProperty(ref _currency, value);
    }

    private int _displayOrder = 0;
    public int DisplayOrder
    {
        get => _displayOrder;
        set => SetProperty(ref _displayOrder, value);
    }

    private bool _isDeleted = false;
    public bool IsDeleted
    {
        get => _isDeleted;
        set => SetProperty(ref _isDeleted, value);
    }

    /// <summary>
    /// P0 (S1): comisión opcional capturada al crear el método. El backend crea el método y su
    /// PaymentMethodFeeRule en la misma operación lógica; una comisión 0/0 no se envía.
    /// </summary>
    public PaymentMethodCommissionClientDto? Commission { get; set; }
}

public sealed class PaymentMethodCommissionClientDto
{
    /// <summary>0=Percentage, 1=Fixed, 2=PercentagePlusFixed (contrato del backend).</summary>
    public int FeeType { get; set; }

    public decimal? Percentage { get; set; }

    public decimal? FixedAmount { get; set; }

    public string CurrencyCode { get; set; } = "USD";

    public bool IsActive { get; set; } = true;
}

public interface IPaymentService
{
    Task<IEnumerable<PaymentMethodDto>> GetActiveMethodsAsync();
    Task<IEnumerable<PaymentMethodDto>> GetAllMethodsAsync();
    Task<PaymentMethodDto> CreateAsync(PaymentMethodDto method);
    Task<PaymentMethodDto> UpdateAsync(PaymentMethodDto method);
    Task DeleteAsync(int id);
    void InvalidateCache();
}
