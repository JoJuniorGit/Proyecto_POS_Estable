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
