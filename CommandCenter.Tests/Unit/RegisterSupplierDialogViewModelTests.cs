using Desktop.Client.ViewModels;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.161: modal de alta de proveedor (RIF/NIT + nombre comercial). Solo captura y valida;
/// el registro lo ejecuta el ViewModel padre al cerrar con resultado true.
/// </summary>
public sealed class RegisterSupplierDialogViewModelTests
{
    [Fact]
    public void Save_WithValidData_TrimsAndRequestsCloseTrue()
    {
        var viewModel = new RegisterSupplierDialogViewModel(" J-123 ", " Proveedor X ");
        bool? result = null;
        viewModel.RequestClose += closed => result = closed;

        viewModel.SaveCommand.Execute(null);

        Assert.True(result);
        Assert.Equal("J-123", viewModel.RifOrNit);
        Assert.Equal("Proveedor X", viewModel.CommercialName);
    }

    [Fact]
    public void Save_WithEmptyCommercialName_ShowsErrorAndDoesNotClose()
    {
        var viewModel = new RegisterSupplierDialogViewModel("J-123", "   ");
        var closed = false;
        viewModel.RequestClose += _ => closed = true;

        viewModel.SaveCommand.Execute(null);

        Assert.False(closed);
        Assert.Contains("nombre comercial", viewModel.ErrorMessage, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_WithoutRif_AllowsRegistrationWithNameOnly()
    {
        var viewModel = new RegisterSupplierDialogViewModel(null, "Solo Nombre");
        bool? result = null;
        viewModel.RequestClose += closed => result = closed;

        viewModel.SaveCommand.Execute(null);

        Assert.True(result);
        Assert.Equal(string.Empty, viewModel.RifOrNit);
    }

    [Fact]
    public void Cancel_RequestsCloseFalse()
    {
        var viewModel = new RegisterSupplierDialogViewModel("J-1", "Proveedor");
        bool? result = null;
        viewModel.RequestClose += closed => result = closed;

        viewModel.CancelCommand.Execute(null);

        Assert.False(result);
    }
}
