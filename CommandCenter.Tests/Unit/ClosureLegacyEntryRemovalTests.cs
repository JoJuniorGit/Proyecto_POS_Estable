using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using CommandCenter.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ClosureLegacyEntryRemovalTests
{
    [Fact]
    public void PublicSurface_ExposesNoEntityReturningClosureEntryPoint()
    {
        var publicMethods = typeof(DailyClosureService).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

        Assert.DoesNotContain(publicMethods, m => m.ReturnType == typeof(DailyClosure));
        Assert.DoesNotContain(publicMethods, m => m.GetParameters().Any(p => p.ParameterType == typeof(DailyClosure)));

        var commandEntry = typeof(DailyClosureService).GetMethod(nameof(DailyClosureService.CreateClosureFromCommandAsync));
        Assert.NotNull(commandEntry);
        Assert.Equal(typeof(Task<CloseShiftResult>), commandEntry!.ReturnType);
    }

    [Theory]
    [InlineData("CreateClosureAsync")]
    [InlineData("ExecuteClosureCoreAsync")]
    [InlineData("MergeMissingMethodsIntoClosure")]
    public void LegacyClosureMembers_AreAbsent(string memberName)
    {
        var members = typeof(DailyClosureService).GetMember(
            memberName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

        Assert.Empty(members);
    }

    [Fact]
    public async Task CreateClosureFromCommand_WithDuplicatedDeclaredMethods_ThrowsDuplicados()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
        var service = DailyClosureTestHelper.CreateService(context);

        var command = new CreateClosureCommand(
            DateTime.UtcNow,
            "Admin",
            null,
            new List<DeclaredPaymentAmount>
            {
                new(1, 100m),
                new(1, 50m)
            });

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateClosureFromCommandAsync(command, CancellationToken.None));

        Assert.Contains("duplicados", exception.Message);
        Assert.Empty(await context.DailyClosures.ToListAsync());
    }
}
