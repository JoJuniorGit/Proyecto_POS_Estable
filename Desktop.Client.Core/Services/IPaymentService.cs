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

    private bool _is_active = true;
    public bool IsActive
    {
        get => _is_active;
        set => SetProperty(ref _is_active, value);
    }

    private bool _requires_reference = false;
    public bool RequiresReference
    {
        get => _requires_reference;
        set => SetProperty(ref _requires_reference, value);
    }

    private bool _is_cash = false;
    public bool IsCash
    {
        get => _is_cash;
        set => SetProperty(ref _is_cash, value);
    }

    private int _display_order = 0;
    public int DisplayOrder
    {
        get => _display_order;
        set => SetProperty(ref _display_order, value);
    }
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
