using Core.Entities;
using Desktop.Client.Navigation;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Role matrix for the WPF sidebar. It mirrors the web role views and adds the WPF-only
/// inventory surfaces, so a hidden item is also rejected by the navigation commands.
/// </summary>
public sealed class WpfNavigationPolicyTests
{
    [Theory]
    [InlineData(UserRole.Admin, WpfNavigationPolicy.Pos, true)]
    [InlineData(UserRole.Admin, WpfNavigationPolicy.Users, true)]
    [InlineData(UserRole.Admin, WpfNavigationPolicy.StockCount, true)]
    [InlineData(UserRole.Manager, WpfNavigationPolicy.Users, false)]
    [InlineData(UserRole.Manager, WpfNavigationPolicy.Settings, true)]
    [InlineData(UserRole.Manager, WpfNavigationPolicy.StockCount, true)]
    [InlineData(UserRole.Cashier, WpfNavigationPolicy.Pos, true)]
    [InlineData(UserRole.Cashier, WpfNavigationPolicy.StockCount, true)]
    [InlineData(UserRole.Cashier, WpfNavigationPolicy.Catalog, true)]
    [InlineData(UserRole.Cashier, WpfNavigationPolicy.Kardex, false)]
    [InlineData(UserRole.Cashier, WpfNavigationPolicy.Import, false)]
    [InlineData(UserRole.Cashier, WpfNavigationPolicy.SupplierInvoices, false)]
    [InlineData(UserRole.Cashier, WpfNavigationPolicy.Settings, false)]
    [InlineData(UserRole.Cashier, WpfNavigationPolicy.Exchange, false)]
    [InlineData(UserRole.Cashier, WpfNavigationPolicy.Users, false)]
    [InlineData(UserRole.Driver, WpfNavigationPolicy.Pickups, true)]
    [InlineData(UserRole.Driver, WpfNavigationPolicy.History, true)]
    [InlineData(UserRole.Driver, WpfNavigationPolicy.Pos, false)]
    [InlineData(UserRole.Driver, WpfNavigationPolicy.Register, false)]
    [InlineData(UserRole.Driver, WpfNavigationPolicy.StockCount, false)]
    [InlineData(UserRole.Driver, WpfNavigationPolicy.Catalog, false)]
    public void CanAccess_MatchesTheRoleMatrix(UserRole role, string view, bool expected)
        => Assert.Equal(expected, WpfNavigationPolicy.CanAccess(role, view));

    [Fact]
    public void CanAccess_WithoutRoleOrView_IsDenied()
    {
        Assert.False(WpfNavigationPolicy.CanAccess(null, WpfNavigationPolicy.Pos));
        Assert.False(WpfNavigationPolicy.CanAccess(UserRole.Admin, null));
        Assert.False(WpfNavigationPolicy.CanAccess(UserRole.Admin, " "));
    }

    [Theory]
    [InlineData(UserRole.Admin, WpfNavigationCategory.Administration, true)]
    [InlineData(UserRole.Manager, WpfNavigationCategory.Administration, true)]
    [InlineData(UserRole.Cashier, WpfNavigationCategory.Administration, false)]
    [InlineData(UserRole.Cashier, WpfNavigationCategory.Inventory, true)]
    [InlineData(UserRole.Cashier, WpfNavigationCategory.Principal, true)]
    [InlineData(UserRole.Driver, WpfNavigationCategory.Principal, true)]
    [InlineData(UserRole.Driver, WpfNavigationCategory.Operations, true)]
    [InlineData(UserRole.Driver, WpfNavigationCategory.Inventory, false)]
    [InlineData(UserRole.Driver, WpfNavigationCategory.Administration, false)]
    public void CanAccessCategory_MatchesTheVisibleItems(
        UserRole role,
        WpfNavigationCategory category,
        bool expected)
        => Assert.Equal(expected, WpfNavigationPolicy.CanAccessCategory(role, category));

    [Theory]
    [InlineData(UserRole.Admin, WpfNavigationPolicy.Pos)]
    [InlineData(UserRole.Manager, WpfNavigationPolicy.Pos)]
    [InlineData(UserRole.Cashier, WpfNavigationPolicy.Pos)]
    [InlineData(UserRole.Driver, WpfNavigationPolicy.Pickups)]
    public void GetFallbackView_ReturnsAnAllowedLandingView(UserRole role, string expected)
    {
        var fallback = WpfNavigationPolicy.GetFallbackView(role);

        Assert.Equal(expected, fallback);
        Assert.True(WpfNavigationPolicy.CanAccess(role, fallback));
    }

    [Fact]
    public void GetFallbackView_WithoutRole_IsNull()
        => Assert.Null(WpfNavigationPolicy.GetFallbackView(null));
}
