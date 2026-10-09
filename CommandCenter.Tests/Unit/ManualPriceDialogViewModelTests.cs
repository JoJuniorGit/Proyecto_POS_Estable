using Desktop.Client.ViewModels;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.151 (W4, R8/design D7): dialogo de precio manual del POS. Valida al menos un monto positivo,
/// rechaza negativos/cero/invalidos y deriva la moneda faltante con la tasa vigente.
/// </summary>
public class ManualPriceDialogViewModelTests
{
    private const decimal Rate = 50m;

    private static ManualPriceDialogViewModel CreateViewModel(decimal rate = Rate)
        => new(rate);

    [Fact]
    public void Accept_WithBothAmounts_UsesEnteredValuesWithoutDerivation()
    {
        var vm = CreateViewModel();
        vm.UnitPriceUsdText = "10";
        vm.UnitPriceBsText = "512.34";

        vm.AcceptCommand.Execute(null);

        Assert.NotNull(vm.Result);
        Assert.Equal(10m, vm.Result!.UnitPriceUsd);
        Assert.Equal(512.34m, vm.Result.UnitPriceLocal);
        Assert.Equal(string.Empty, vm.ErrorMessage);
        Assert.False(vm.HasError);
    }

    [Fact]
    public void Accept_WithOnlyUsd_DerivesLocalWithCeilingRounding()
    {
        var vm = CreateViewModel(804.63m);
        vm.UnitPriceUsdText = "0.81";

        vm.AcceptCommand.Execute(null);

        Assert.NotNull(vm.Result);
        Assert.Equal(0.81m, vm.Result!.UnitPriceUsd);
        Assert.Equal(651.76m, vm.Result.UnitPriceLocal);
    }

    [Fact]
    public void Accept_WithOnlyLocal_DerivesUsdRoundedAwayFromZero()
    {
        var vm = CreateViewModel(50m);
        vm.UnitPriceBsText = "125.55";

        vm.AcceptCommand.Execute(null);

        Assert.NotNull(vm.Result);
        Assert.Equal(2.51m, vm.Result!.UnitPriceUsd);
        Assert.Equal(125.55m, vm.Result.UnitPriceLocal);
    }

    [Theory]
    [InlineData("0", "")]
    [InlineData("-5", "")]
    [InlineData("abc", "")]
    [InlineData("", "0")]
    [InlineData("", "-3.5")]
    [InlineData("", "xyz")]
    public void Accept_WithZeroNegativeOrInvalidAmount_RejectsWithInlineError(string usd, string bs)
    {
        var vm = CreateViewModel();
        vm.UnitPriceUsdText = usd;
        vm.UnitPriceBsText = bs;

        vm.AcceptCommand.Execute(null);

        Assert.Null(vm.Result);
        Assert.Equal(ManualPriceDialogViewModel.InvalidAmountMessage, vm.ErrorMessage);
        Assert.True(vm.HasError);
    }

    [Fact]
    public void Accept_WithBothEmpty_ShowsMissingAmountError()
    {
        var vm = CreateViewModel();

        vm.AcceptCommand.Execute(null);

        Assert.Null(vm.Result);
        Assert.Equal(ManualPriceDialogViewModel.MissingAmountMessage, vm.ErrorMessage);
    }

    [Fact]
    public void Accept_WithWhitespaceOnlyFields_TreatsThemAsOmitted()
    {
        var vm = CreateViewModel();
        vm.UnitPriceUsdText = "  10  ";
        vm.UnitPriceBsText = "   ";

        vm.AcceptCommand.Execute(null);

        Assert.NotNull(vm.Result);
        Assert.Equal(500m, vm.Result!.UnitPriceLocal);
    }

    [Fact]
    public void Accept_WithSingleCurrencyWithoutValidRate_ShowsRateError()
    {
        var vm = CreateViewModel(0m);
        vm.UnitPriceUsdText = "10";

        vm.AcceptCommand.Execute(null);

        Assert.Null(vm.Result);
        Assert.Equal(ManualPriceDialogViewModel.MissingRateMessage, vm.ErrorMessage);
    }

    [Fact]
    public void Accept_WithBothAmountsWithoutValidRate_SucceedsWithoutDerivation()
    {
        var vm = CreateViewModel(0m);
        vm.UnitPriceUsdText = "10";
        vm.UnitPriceBsText = "500";

        vm.AcceptCommand.Execute(null);

        Assert.NotNull(vm.Result);
        Assert.Equal(10m, vm.Result!.UnitPriceUsd);
        Assert.Equal(500m, vm.Result.UnitPriceLocal);
    }

    [Fact]
    public void Accept_WithCommaDecimalSeparator_ParsesValue()
    {
        var vm = CreateViewModel(10m);
        vm.UnitPriceUsdText = "1,5";

        vm.AcceptCommand.Execute(null);

        Assert.NotNull(vm.Result);
        Assert.Equal(1.5m, vm.Result!.UnitPriceUsd);
        Assert.Equal(15m, vm.Result.UnitPriceLocal);
    }

    [Fact]
    public void Accept_AfterInvalidAttempt_ClearsPreviousErrorWhenValid()
    {
        var vm = CreateViewModel();
        vm.UnitPriceUsdText = "abc";
        vm.AcceptCommand.Execute(null);
        Assert.True(vm.HasError);

        vm.UnitPriceUsdText = "10";
        vm.AcceptCommand.Execute(null);

        Assert.NotNull(vm.Result);
        Assert.Equal(string.Empty, vm.ErrorMessage);
        Assert.False(vm.HasError);
    }

    [Fact]
    public void Cancel_LeavesResultNullWithoutValidating()
    {
        var vm = CreateViewModel();
        vm.UnitPriceUsdText = "not-a-number";

        vm.CancelCommand.Execute(null);

        Assert.Null(vm.Result);
        Assert.Equal(string.Empty, vm.ErrorMessage);
    }
}
