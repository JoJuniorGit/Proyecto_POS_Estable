using Backend.API.Attributes;
using Backend.API.Controllers;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.177-T2 (S2/S3): contrato del endpoint del Kardex — roles, sello de seguridad, rutas,
/// forwarding de filtros y content-types de exportación.
/// </summary>
public sealed class StockKardexControllerTests
{
    private static Mock<IStockKardexService> CreateServiceMock() => new();

    [Fact]
    public void Controller_IsRestrictedToAdminAndManager_WithSecurityStampValidation()
    {
        var controllerType = typeof(StockMovementsController);

        var authorize = (AuthorizeAttribute?)Attribute.GetCustomAttribute(controllerType, typeof(AuthorizeAttribute));
        Assert.NotNull(authorize);
        Assert.Equal("Admin,Manager", authorize!.Roles);

        var allowedRoles = authorize.Roles!.Split(',', StringSplitOptions.TrimEntries);
        Assert.Contains("Admin", allowedRoles);
        Assert.Contains("Manager", allowedRoles);
        Assert.DoesNotContain("Cashier", allowedRoles);
        Assert.DoesNotContain("Driver", allowedRoles);

        Assert.NotNull(Attribute.GetCustomAttribute(controllerType, typeof(RequireSecurityStampValidationAttribute)));
    }

    [Fact]
    public void Controller_Routes_MatchKardexContract()
    {
        var route = (RouteAttribute?)Attribute.GetCustomAttribute(typeof(StockMovementsController), typeof(RouteAttribute));
        Assert.NotNull(route);
        Assert.Equal("api/stock-movements", route!.Template);

        var listGet = typeof(StockMovementsController)
            .GetMethod(nameof(StockMovementsController.GetMovementsAsync))!
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .Single();
        Assert.True(string.IsNullOrEmpty(listGet.Template));

        var summaryGet = typeof(StockMovementsController)
            .GetMethod(nameof(StockMovementsController.GetSummaryAsync))!
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .Single();
        Assert.Equal("summary", summaryGet.Template);

        var exportGet = typeof(StockMovementsController)
            .GetMethod(nameof(StockMovementsController.ExportAsync))!
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .Single();
        Assert.Equal("export", exportGet.Template);
    }

    [Fact]
    public async Task GetMovementsAsync_ForwardsFilterAndPagination_ToService()
    {
        var expected = new PagedResultDto<StockKardexRowDto>(new List<StockKardexRowDto>(), 0);
        StockKardexFilterDto? captured = null;
        var service = CreateServiceMock();
        service.Setup(s => s.GetMovementsAsync(It.IsAny<StockKardexFilterDto>(), 2, 50, It.IsAny<CancellationToken>()))
            .Callback<StockKardexFilterDto, int, int, CancellationToken>((f, _, _, _) => captured = f)
            .ReturnsAsync(expected);
        var controller = new StockMovementsController(service.Object);

        var result = await controller.GetMovementsAsync(
            dateFromUtc: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            userId: "cashier-1",
            productId: 7,
            reasonCodes: "Damage,4",
            originTypes: "Manual",
            direction: StockKardexDirection.Out,
            page: 2,
            pageSize: 50);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(expected, ok.Value);
        Assert.NotNull(captured);
        Assert.Equal(7, captured!.ProductId);
        Assert.Equal("cashier-1", captured.UserId);
        Assert.Equal(StockKardexDirection.Out, captured.Direction);
        Assert.Equal(new[] { StockMovementReason.Damage, StockMovementReason.PhysicalCountCorrection }, captured.ReasonCodes);
        Assert.Equal(new[] { StockMovementOriginType.Manual }, captured.OriginTypes);
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), captured.DateFromUtc);
    }

    [Fact]
    public async Task GetMovementsAsync_InvalidReasonCode_Returns400_WithoutCallingService()
    {
        var service = CreateServiceMock();
        var controller = new StockMovementsController(service.Object);

        var result = await controller.GetMovementsAsync(reasonCodes: "NoExiste");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, badRequest.StatusCode);
        service.Verify(
            s => s.GetMovementsAsync(It.IsAny<StockKardexFilterDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetSummaryAsync_ReturnsServiceSummary()
    {
        var summary = new StockKardexSummaryDto
        {
            TotalMovements = 3,
            NetVolume = -2m,
            EntriesValueUsd = 1m,
            LossesValueUsd = 2m,
            NetValueUsd = -1m,
            UnvaluedMovements = 1
        };
        var service = CreateServiceMock();
        service.Setup(s => s.GetSummaryAsync(It.IsAny<StockKardexFilterDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);
        var controller = new StockMovementsController(service.Object);

        var result = await controller.GetSummaryAsync(direction: StockKardexDirection.Out);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(summary, ok.Value);
    }

    [Theory]
    [InlineData("csv", "text/csv; charset=utf-8", ".csv")]
    [InlineData("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx")]
    [InlineData("pdf", "application/pdf", ".pdf")]
    public async Task ExportAsync_ReturnsFileWithFormatContract(string format, string expectedContentType, string expectedExtension)
    {
        var bytes = new byte[] { 1, 2, 3 };
        var service = CreateServiceMock();
        service.Setup(s => s.ExportAsync(It.IsAny<StockKardexFilterDto>(), format, It.IsAny<CancellationToken>()))
            .ReturnsAsync(bytes);
        var controller = new StockMovementsController(service.Object);

        var result = await controller.ExportAsync(format: format);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(expectedContentType, file.ContentType);
        Assert.Equal(bytes, file.FileContents);
        Assert.StartsWith("kardex-", file.FileDownloadName);
        Assert.EndsWith(expectedExtension, file.FileDownloadName);
    }

    [Fact]
    public async Task ExportAsync_InvalidFormat_Returns400_WithoutCallingService()
    {
        var service = CreateServiceMock();
        var controller = new StockMovementsController(service.Object);

        var result = await controller.ExportAsync(format: "docx");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        service.Verify(
            s => s.ExportAsync(It.IsAny<StockKardexFilterDto>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExportAsync_ForwardsNormalizedFormatAndFilter_ToService()
    {
        StockKardexFilterDto? captured = null;
        var service = CreateServiceMock();
        service.Setup(s => s.ExportAsync(It.IsAny<StockKardexFilterDto>(), "csv", It.IsAny<CancellationToken>()))
            .Callback<StockKardexFilterDto, string, CancellationToken>((f, _, _) => captured = f)
            .ReturnsAsync(new byte[] { 0x50 });
        var controller = new StockMovementsController(service.Object);

        await controller.ExportAsync(format: "CSV", productId: 9, direction: StockKardexDirection.In);

        Assert.NotNull(captured);
        Assert.Equal(9, captured!.ProductId);
        Assert.Equal(StockKardexDirection.In, captured.Direction);
        service.Verify(s => s.ExportAsync(It.IsAny<StockKardexFilterDto>(), "csv", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SettingsController_CompanyNameRoutes_MatchContract()
    {
        var getMethod = typeof(SettingsController).GetMethod(nameof(SettingsController.GetCompanyNameAsync))!;
        var getAttribute = getMethod
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .Single();
        Assert.Equal("company-name", getAttribute.Template);

        var postMethod = typeof(SettingsController).GetMethod(nameof(SettingsController.SetCompanyNameAsync))!;
        var postAttribute = postMethod
            .GetCustomAttributes(typeof(HttpPostAttribute), inherit: false)
            .Cast<HttpPostAttribute>()
            .Single();
        Assert.Equal("company-name", postAttribute.Template);

        var authorize = (AuthorizeAttribute?)Attribute.GetCustomAttribute(postMethod, typeof(AuthorizeAttribute));
        Assert.NotNull(authorize);
        Assert.Equal("Admin", authorize!.Roles);
    }
}
