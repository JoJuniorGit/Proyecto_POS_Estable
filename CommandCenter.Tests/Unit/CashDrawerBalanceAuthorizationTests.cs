using System;
using System.Linq;
using Backend.API.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class CashDrawerBalanceAuthorizationTests
{
    [Fact]
    public void GetCurrentBalanceAsync_IsRestrictedToAdminAndManager_WithoutCashier()
    {
        var method = typeof(CashDrawerController).GetMethod(nameof(CashDrawerController.GetCurrentBalanceAsync));

        Assert.NotNull(method);
        Assert.Null(Attribute.GetCustomAttribute(method!, typeof(AllowAnonymousAttribute)));

        var authorize = (AuthorizeAttribute?)Attribute.GetCustomAttribute(method!, typeof(AuthorizeAttribute));
        Assert.NotNull(authorize);
        Assert.Equal("Admin,Manager", authorize!.Roles);

        var allowedRoles = authorize.Roles?.Split(',', StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
        Assert.Contains("Admin", allowedRoles);
        Assert.Contains("Manager", allowedRoles);
        Assert.DoesNotContain("Cashier", allowedRoles);
        Assert.DoesNotContain("Driver", allowedRoles);
    }

    [Fact]
    public void GetCurrentBalanceAsync_PreservesHttpGetRouteAndSessionIdQuery()
    {
        var method = typeof(CashDrawerController).GetMethod(nameof(CashDrawerController.GetCurrentBalanceAsync));

        Assert.NotNull(method);
        var httpGet = method!.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .SingleOrDefault();

        Assert.NotNull(httpGet);
        Assert.Equal("current-balance", httpGet!.Template);

        var sessionIdParameter = method.GetParameters().Single(p => p.Name == "sessionId");
        Assert.Equal(typeof(int), sessionIdParameter.ParameterType);
    }
}
