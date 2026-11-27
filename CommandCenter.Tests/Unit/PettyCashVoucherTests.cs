using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Receipts;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// T3 (Paso 13 / 8.188, S7): vale de caja chica on-demand. Cubre el contexto del comprobante
/// (correlativo VCC, fecha/hora de Venezuela, categoria con label, montos duales con la tasa
/// congelada, cajero/autorizador y sello de anulacion), el PDF no vacio, la resolucion de
/// nombres del servicio y el contrato del endpoint (ownership del cajero creador, bypass
/// Admin/Manager, 404 de gasto inexistente). SQLite relacional para el servicio y el controlador.
/// </summary>
public class PettyCashVoucherTests
{
    private const decimal Rate = 36.55m;

    private static readonly DateTime CreatedAtUtc = new(2026, 10, 18, 13, 45, 0, DateTimeKind.Utc);
    private static readonly DateTime SessionOpenedAtUtc = new(2026, 10, 18, 13, 0, 0, DateTimeKind.Utc);

    /// <summary>Cajero creador del gasto (ownership del vale) y Gerente autorizador.</summary>
    private const int CashierUserId = 70;
    private const int ManagerUserId = 2;

    private static MonetaryProfileDto DualUsdCopProfile() => new()
    {
        Mode = MonetaryMode.Dual,
        PrimaryCurrencyCode = "USD",
        PrimaryCurrencySymbol = "$",
        SecondaryCurrencyCode = "COP",
        SecondaryCurrencySymbol = "COL$",
        IsConfigured = true
    };

    private sealed class VoucherStack : IDisposable
    {
        private readonly SqliteConnection _connection;

        public SalesDbContext Context { get; }
        public PettyCashService Service { get; }

        public VoucherStack(IMonetaryProfileService? profileService = null)
        {
            (Context, _connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();

            var inventory = new Mock<IInventoryService>();
            inventory.Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Rate);
            var settings = new Mock<ISystemSettingsService>();
            settings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

            Service = new PettyCashService(
                Context,
                new CashDrawerService(Context),
                inventory.Object,
                settings.Object,
                logger: null,
                monetaryProfileService: profileService);
        }

        public void Dispose()
        {
            Context.Dispose();
            _connection.Dispose();
        }
    }

    private static PettyCashExpense BuildExpense(
        long voucherNumber = 42L,
        PettyCashCurrency currency = PettyCashCurrency.BsS,
        decimal amountBsS = 365.50m,
        decimal amountUsd = 10.0000m,
        PettyCashCategory category = PettyCashCategory.InsumosLimpieza,
        string concept = "Compra de jabon y cloro",
        string beneficiary = "Limpieza Total C.A.",
        string reference = "FACT-0001",
        int? authorizedByUserId = ManagerUserId,
        PettyCashStatus status = PettyCashStatus.Active,
        DateTime? cancelledAtUtc = null,
        int? cancelledByUserId = null,
        int? cancellationAuthorizedByUserId = null,
        string? cancellationReason = null)
    {
        return new PettyCashExpense
        {
            Id = 7,
            VoucherNumber = voucherNumber,
            SessionId = 55,
            Category = category,
            Currency = currency,
            AmountBsS = amountBsS,
            AmountUsd = amountUsd,
            HistoricalRate = Rate,
            Concept = concept,
            Beneficiary = beneficiary,
            ReferenceNumber = reference,
            CreatedByUserId = CashierUserId,
            CreatedAtUtc = CreatedAtUtc,
            AuthorizedByUserId = authorizedByUserId,
            Status = status,
            CancelledAtUtc = cancelledAtUtc,
            CancelledByUserId = cancelledByUserId,
            CancellationAuthorizedByUserId = cancellationAuthorizedByUserId,
            CancellationReason = cancellationReason,
            Session = new CashDrawerSession
            {
                Id = 55,
                OpenedAt = SessionOpenedAtUtc,
                Status = CashDrawerStatus.Open,
                OpeningBalanceLocal = 1000m,
                OpeningExchangeRate = Rate
            }
        };
    }

    private static async Task SeedUsersAsync(SalesDbContext db)
    {
        db.Users.AddRange(
            new User { Id = ManagerUserId, Cedula = "V-00000002", Name = "Gerente Dos", Username = "gerente2", FullName = "Gerente Dos", Role = UserRole.Manager },
            new User { Id = CashierUserId, Cedula = "V-00000070", Name = "Cajero 70", Username = "usuario70", FullName = "Cajero 70", Role = UserRole.Cashier },
            new User { Id = 71, Cedula = "V-00000071", Name = "Cajero 71", Username = "usuario71", FullName = "Cajero 71", Role = UserRole.Cashier });
        await db.SaveChangesAsync();
    }

    private static async Task<PettyCashExpense> SeedExpenseAsync(
        SalesDbContext db,
        PettyCashStatus status = PettyCashStatus.Active,
        int? cancelledByUserId = null,
        string? cancellationReason = null,
        int? cancellationAuthorizedByUserId = null)
    {
        await SeedUsersAsync(db);

        var session = new CashDrawerSession
        {
            OpenedAt = SessionOpenedAtUtc,
            Status = CashDrawerStatus.Open,
            OpeningBalanceLocal = 1000m,
            OpeningExchangeRate = Rate
        };
        db.CashDrawerSessions.Add(session);
        await db.SaveChangesAsync();

        var expense = BuildExpense(
            voucherNumber: 1L,
            status: status,
            cancelledAtUtc: status == PettyCashStatus.Cancelled
                ? new DateTime(2026, 10, 18, 15, 30, 0, DateTimeKind.Utc)
                : null,
            cancelledByUserId: cancelledByUserId,
            cancellationAuthorizedByUserId: cancellationAuthorizedByUserId,
            cancellationReason: cancellationReason);
        expense.Session = null!;
        expense.SessionId = session.Id;
        expense.CreatedAtUtc = CreatedAtUtc;
        db.PettyCashExpenses.Add(expense);
        await db.SaveChangesAsync();
        return expense;
    }

    private static void AttachUser(PettyCashController controller, string userId, string? role)
    {
        var claims = new System.Collections.Generic.List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.NameIdentifier, userId)
        };
        if (role != null)
        {
            claims.Add(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role));
        }

        var user = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(claims, "mock"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
    }

    private static PettyCashController CreateController(VoucherStack stack, string userId, UserRole role)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(service => service.UserId).Returns(userId);
        currentUser.SetupGet(service => service.UserRole).Returns(role);

        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync("CurrencyFormat")).ReturnsAsync("Venezuelan");

        var controller = new PettyCashController(stack.Service, currentUser.Object, null, settings.Object);
        AttachUser(controller, userId, role.ToString());
        return controller;
    }

    // ------------------------------------------------------------------ contexto del vale

    [Fact]
    public void CreateFrom_BuildsVoucherIdentityWithVenezuelaTimes()
    {
        var context = PettyCashVoucherContext.CreateFrom(BuildExpense(), "Cajero 70", "Gerente Dos");

        Assert.Equal(7, context.ExpenseId);
        Assert.Equal(42L, context.VoucherNumber);
        Assert.Equal("VCC-000042", context.VoucherCode);
        Assert.Equal(55, context.SessionId);
        // UTC-4 (hora legal de Venezuela, sin DST): turno 09:00 y vale 09:45.
        Assert.Equal(new DateTime(2026, 10, 18, 9, 0, 0), context.SessionOpenedAtVenezuela);
        Assert.Equal(new DateTime(2026, 10, 18, 9, 45, 0), context.CreatedAtVenezuela);
        Assert.Equal(PettyCashCategory.InsumosLimpieza, context.Category);
        Assert.Equal("Insumos y Limpieza", context.CategoryLabel);
        Assert.Equal("Compra de jabon y cloro", context.Concept);
        Assert.Equal("Limpieza Total C.A.", context.Beneficiary);
        Assert.Equal("FACT-0001", context.ReferenceNumber);
    }

    [Fact]
    public void CreateFrom_BsSExpense_UsesLocalNominalAndFrozenDualValuation()
    {
        var context = PettyCashVoucherContext.CreateFrom(BuildExpense(), "Cajero 70", "Gerente Dos");

        Assert.Equal(PettyCashCurrency.BsS, context.Currency);
        Assert.Equal(365.50m, context.NominalAmount);
        Assert.Equal(365.50m, context.AmountBsS);
        Assert.Equal(10.0000m, context.AmountUsd);
        Assert.Equal(Rate, context.HistoricalRate);
        Assert.Equal("Cajero 70", context.CashierName);
        Assert.Equal("Gerente Dos", context.AuthorizerName);
        Assert.False(context.IsCancelled);
    }

    [Fact]
    public void CreateFrom_UsdExpense_UsesUsdAmountAsNominal()
    {
        var context = PettyCashVoucherContext.CreateFrom(
            BuildExpense(currency: PettyCashCurrency.Usd, amountBsS: 731.00m, amountUsd: 20.0000m),
            "Cajero 70",
            "Gerente Dos");

        Assert.Equal(PettyCashCurrency.Usd, context.Currency);
        Assert.Equal(20.0000m, context.NominalAmount);
        Assert.Equal(731.00m, context.AmountBsS);
        Assert.Equal(20.0000m, context.AmountUsd);
    }

    [Fact]
    public void CreateFrom_LegacyBsSExpense_UsesVesCodeAndBsSSymbol()
    {
        var context = PettyCashVoucherContext.CreateFrom(BuildExpense(), "Cajero 70", "Gerente Dos");

        Assert.Equal("VES", context.CurrencyCode);
        Assert.Equal("Bs.S", context.CurrencySymbol);
        Assert.Equal("VES", context.LocalCurrencyCode);
        Assert.Equal("Bs.S", context.LocalCurrencySymbol);
        Assert.Equal("$", context.UsdCurrencySymbol);
    }

    [Fact]
    public void CreateFrom_DualCopProfile_UsesPersistedMovementCurrencyAndConfiguredSymbol()
    {
        var context = PettyCashVoucherContext.CreateFrom(
            BuildExpense(currency: PettyCashCurrency.BsS, amountBsS: 365.50m, amountUsd: 0.0914m),
            "Cajero 70",
            "Gerente Dos",
            profile: DualUsdCopProfile(),
            persistedCurrencyCode: "COP");

        Assert.Equal("COP", context.CurrencyCode);
        Assert.Equal("COL$", context.CurrencySymbol);
        Assert.Equal("COP", context.LocalCurrencyCode);
        Assert.Equal("COL$", context.LocalCurrencySymbol);
        Assert.Equal("$", context.UsdCurrencySymbol);

        var content = Encoding.ASCII.GetString(
            PettyCashVoucherPdfGenerator.BuildPdf(context, MoneyDisplayFormat.Venezuelan));
        Assert.Contains("COP", content);
        Assert.Contains("COL$", content);
        Assert.DoesNotContain("Bs.S", content);
    }

    [Fact]
    public void CreateFrom_UsdExpenseInDualCopProfile_KeepsUsdNominalAndCopLocal()
    {
        var context = PettyCashVoucherContext.CreateFrom(
            BuildExpense(currency: PettyCashCurrency.Usd, amountBsS: 73000m, amountUsd: 20.0000m),
            "Cajero 70",
            "Gerente Dos",
            profile: DualUsdCopProfile());

        Assert.Equal("USD", context.CurrencyCode);
        Assert.Equal("$", context.CurrencySymbol);
        Assert.Equal("COP", context.LocalCurrencyCode);
        Assert.Equal("COL$", context.LocalCurrencySymbol);

        var content = Encoding.ASCII.GetString(
            PettyCashVoucherPdfGenerator.BuildPdf(context, MoneyDisplayFormat.Venezuelan));
        Assert.Contains(@"MONTO ENTREGADO \($\)", content);
        Assert.Contains("COL$ 73.000,00 / $ 20,0000", content);
        Assert.DoesNotContain("Bs.S", content);
    }

    [Fact]
    public void CreateFrom_CancelledExpense_CarriesCancellationStamp()
    {
        var cancelledAtUtc = new DateTime(2026, 10, 18, 15, 30, 0, DateTimeKind.Utc);
        var expense = BuildExpense(
            status: PettyCashStatus.Cancelled,
            cancelledAtUtc: cancelledAtUtc,
            cancelledByUserId: 1,
            cancellationReason: "Proveedor no entrego el pedido");

        var context = PettyCashVoucherContext.CreateFrom(expense, "Cajero 70", "Gerente Dos", "Administrador");

        Assert.True(context.IsCancelled);
        Assert.Equal(PettyCashStatus.Cancelled, context.Status);
        Assert.Equal(new DateTime(2026, 10, 18, 11, 30, 0), context.CancelledAtVenezuela);
        Assert.Equal("Administrador", context.CancelledByName);
        Assert.Equal("Proveedor no entrego el pedido", context.CancellationReason);
        // Sin supervisor aprobador registrado no hay linea de autorizacion de la anulacion.
        Assert.Null(context.CancellationAuthorizedName);
    }

    [Fact]
    public void CreateFrom_CancelledWithCancellationAuthorizer_ExposesDistinctSupervisor()
    {
        var expense = BuildExpense(
            status: PettyCashStatus.Cancelled,
            cancelledAtUtc: new DateTime(2026, 10, 18, 15, 30, 0, DateTimeKind.Utc),
            cancelledByUserId: ManagerUserId,
            cancellationAuthorizedByUserId: 1,
            cancellationReason: "Proveedor no entrego el pedido");

        var context = PettyCashVoucherContext.CreateFrom(expense, "Cajero 70", "Gerente Dos", "Gerente Dos", "Administrador");

        Assert.True(context.IsCancelled);
        Assert.Equal("Gerente Dos", context.CancelledByName);
        Assert.Equal("Administrador", context.CancellationAuthorizedName);
    }

    [Fact]
    public void CreateFrom_CancellationAuthorizerWithoutUserRow_FallsBackToNotFound()
    {
        var expense = BuildExpense(
            status: PettyCashStatus.Cancelled,
            cancelledAtUtc: new DateTime(2026, 10, 18, 15, 30, 0, DateTimeKind.Utc),
            cancelledByUserId: ManagerUserId,
            cancellationAuthorizedByUserId: 999,
            cancellationReason: "Proveedor no entrego el pedido");

        var context = PettyCashVoucherContext.CreateFrom(expense, "Cajero 70", "Gerente Dos", "Gerente Dos", null);

        // El ID existe pero la fila de usuario no: dato anomalo, mismo criterio "N/D" del resto del vale.
        Assert.Equal(PettyCashVoucherContext.UserNotFound, context.CancellationAuthorizedName);
    }

    [Fact]
    public void CreateFrom_WithoutAuthorizer_ShowsNotApplicable()
    {
        var context = PettyCashVoucherContext.CreateFrom(BuildExpense(authorizedByUserId: null), "Cajero 70", null);

        Assert.Equal("N/A", context.AuthorizerName);
    }

    [Fact]
    public void DescribeCategory_LabelsEveryCategoryInSpanish()
    {
        Assert.Equal("Insumos y Limpieza", PettyCashVoucherContext.DescribeCategory(PettyCashCategory.InsumosLimpieza));
        Assert.Equal("Fletes y Acarreo", PettyCashVoucherContext.DescribeCategory(PettyCashCategory.FletesAcarreo));
        Assert.Equal("Mantenimiento y Reparaciones", PettyCashVoucherContext.DescribeCategory(PettyCashCategory.MantenimientoReparaciones));
        Assert.Equal("Servicios Públicos/Eventuales", PettyCashVoucherContext.DescribeCategory(PettyCashCategory.ServiciosPublicos));
        Assert.Equal("Papelería", PettyCashVoucherContext.DescribeCategory(PettyCashCategory.Papeleria));
        Assert.Equal("Varios", PettyCashVoucherContext.DescribeCategory(PettyCashCategory.Varios));
    }

    // ------------------------------------------------------------------ PDF del vale

    [Fact]
    public void BuildPdf_GeneratesNonEmptyPdfWithVoucherData()
    {
        var context = PettyCashVoucherContext.CreateFrom(BuildExpense(), "Cajero 70", "Gerente Dos");

        var bytes = PettyCashVoucherPdfGenerator.BuildPdf(context, MoneyDisplayFormat.Venezuelan);
        var content = Encoding.ASCII.GetString(bytes);

        Assert.NotEmpty(bytes);
        Assert.StartsWith("%PDF-1.4", content);
        Assert.Contains("VALE DE CAJA CHICA", content);
        Assert.Contains("VCC-000042", content);
        Assert.Contains("18/10/2026 09:45", content);
        Assert.Contains("Insumos y Limpieza", content);
        Assert.Contains("Compra de jabon y cloro", content);
        Assert.Contains("Limpieza Total C.A.", content);
        Assert.Contains("FACT-0001", content);
        // Bs.S 2dp / USD 4dp / tasa 4dp con el formato es-VE de la convencion del POS.
        Assert.Contains("365,50", content);
        Assert.Contains("10,0000", content);
        Assert.Contains("36,5500", content);
        Assert.Contains("Cajero 70", content);
        Assert.Contains("Gerente Dos", content);
        Assert.Contains("Entregado por", content);
        Assert.Contains("Recibido conforme por", content);
    }

    [Fact]
    public void BuildPdf_CancelledVoucher_ShowsCancellationStamp()
    {
        var expense = BuildExpense(
            status: PettyCashStatus.Cancelled,
            cancelledAtUtc: new DateTime(2026, 10, 18, 15, 30, 0, DateTimeKind.Utc),
            cancelledByUserId: 1,
            cancellationReason: "Proveedor no entrego el pedido");
        var context = PettyCashVoucherContext.CreateFrom(expense, "Cajero 70", "Gerente Dos", "Administrador");

        var content = Encoding.ASCII.GetString(
            PettyCashVoucherPdfGenerator.BuildPdf(context, MoneyDisplayFormat.Venezuelan));

        Assert.Contains("ANULADO", content);
        Assert.Contains("18/10/2026 11:30", content);
        Assert.Contains("Administrador", content);
        Assert.Contains("Proveedor no entrego el pedido", content);
        // Sin supervisor aprobador el render queda igual que antes (sin linea de autorizacion).
        Assert.DoesNotContain(@"Autoriz\363 anulaci\363n", content);
    }

    [Fact]
    public void BuildPdf_CancelledWithCancellationAuthorizer_ShowsAuthorizationLine()
    {
        var expense = BuildExpense(
            status: PettyCashStatus.Cancelled,
            cancelledAtUtc: new DateTime(2026, 10, 18, 15, 30, 0, DateTimeKind.Utc),
            cancelledByUserId: ManagerUserId,
            cancellationAuthorizedByUserId: 1,
            cancellationReason: "Proveedor no entrego el pedido");
        var context = PettyCashVoucherContext.CreateFrom(expense, "Cajero 70", "Gerente Dos", "Gerente Dos", "Administrador");

        var content = Encoding.ASCII.GetString(
            PettyCashVoucherPdfGenerator.BuildPdf(context, MoneyDisplayFormat.Venezuelan));

        Assert.Contains("ANULADO", content);
        Assert.Contains("Gerente Dos", content);
        // "Autorizó anulación: ..." viaja escapado en octal por la fuente WinAnsi del generador.
        Assert.Contains(@"Autoriz\363 anulaci\363n: Administrador", content);
    }

    [Fact]
    public void BuildPdf_EscapesEverySpanishCharacterAsWinAnsiOctalWithoutQuestionMarks()
    {
        // Quick win 8.188: el mapeo octal WinAnsi (CP1252) debe cubrir TODO el espanol del
        // documento; ¿, ¡ y ° caian a '?' con el Encoding.ASCII del generador.
        var expense = BuildExpense(
            category: PettyCashCategory.Papeleria,
            concept: "Autorización de café para Ñandú ¿listo? 25°",
            beneficiary: "Señora Ávila ¡Sí!",
            reference: "REF-Ñ-01");
        var context = PettyCashVoucherContext.CreateFrom(expense, "Cajero 70", "Gerente Dos");

        var content = Encoding.ASCII.GetString(
            PettyCashVoucherPdfGenerator.BuildPdf(context, MoneyDisplayFormat.Venezuelan));

        // Formas octales exactas por caracter del mapeo espanol.
        Assert.Contains(@"Autorizaci\363n de caf\351 para \321and\372", content);
        Assert.Contains(@"\277listo? 25\260", content);
        Assert.Contains(@"Se\361ora \301vila \241S\355!", content);
        Assert.Contains(@"Papeler\355a", content);

        // Los textos clave nunca llegan reemplazados por '?' (ni crudos no-ASCII).
        Assert.DoesNotContain("Autorizaci?n", content);
        Assert.DoesNotContain("caf?", content);
        Assert.DoesNotContain("?and", content);
        Assert.DoesNotContain("Se?ora", content);
        Assert.DoesNotContain("?vila", content);
        Assert.DoesNotContain("S?", content);
    }

    // ------------------------------------------------------------------ servicio (nombres + 404)

    [Fact]
    public async Task GetVoucherDataAsync_ResolvesUserNamesAndNullForMissingExpense()
    {
        using var stack = new VoucherStack();
        var expense = await SeedExpenseAsync(stack.Context);

        Assert.Null(await stack.Service.GetVoucherDataAsync(9999));

        var data = await stack.Service.GetVoucherDataAsync(expense.Id);

        Assert.NotNull(data);
        Assert.Equal(CashierUserId, data!.CreatedByUserId);
        Assert.Equal("VCC-000001", data.Context.VoucherCode);
        Assert.Equal(expense.SessionId, data.Context.SessionId);
        Assert.Equal("Cajero 70", data.Context.CashierName);
        Assert.Equal("Gerente Dos", data.Context.AuthorizerName);
        Assert.Equal(new DateTime(2026, 10, 18, 9, 0, 0), data.Context.SessionOpenedAtVenezuela);
        Assert.Equal(new DateTime(2026, 10, 18, 9, 45, 0), data.Context.CreatedAtVenezuela);
        Assert.Equal(365.50m, data.Context.AmountBsS);
        Assert.Equal(10.0000m, data.Context.AmountUsd);
        Assert.Equal(Rate, data.Context.HistoricalRate);
        Assert.False(data.Context.IsCancelled);
    }

    [Fact]
    public async Task GetVoucherDataAsync_WithDualCopProfileAndPersistedMovement_UsesCopLabels()
    {
        var profile = new Mock<IMonetaryProfileService>();
        profile
            .Setup(service => service.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DualUsdCopProfile());
        using var stack = new VoucherStack(profile.Object);
        var expense = await SeedExpenseAsync(stack.Context);

        stack.Context.CashTransactions.Add(new CashTransaction
        {
            SessionId = expense.SessionId,
            TransactionTime = CreatedAtUtc,
            Type = CashTransactionType.Expense,
            Source = CashTransactionSource.PettyCashExpense,
            AmountLocal = expense.AmountBsS,
            AmountUsd = expense.AmountUsd,
            ExchangeRate = Rate,
            PettyCashExpenseId = expense.Id,
            CurrencyCode = "COP"
        });
        await stack.Context.SaveChangesAsync();

        var data = await stack.Service.GetVoucherDataAsync(expense.Id);

        Assert.NotNull(data);
        Assert.Equal("COP", data!.Context.CurrencyCode);
        Assert.Equal("COL$", data.Context.CurrencySymbol);
        Assert.Equal("COP", data.Context.LocalCurrencyCode);
        Assert.Equal("COL$", data.Context.LocalCurrencySymbol);
    }

    [Fact]
    public async Task GetVoucherDataAsync_CancelledExpense_ResolvesCancellerAndKeepsReason()
    {
        using var stack = new VoucherStack();
        var expense = await SeedExpenseAsync(
            stack.Context,
            status: PettyCashStatus.Cancelled,
            cancelledByUserId: 1,
            cancellationReason: "Proveedor no entrego el pedido");

        var data = await stack.Service.GetVoucherDataAsync(expense.Id);

        Assert.NotNull(data);
        Assert.True(data!.Context.IsCancelled);
        Assert.Equal("Administrador", data.Context.CancelledByName);
        Assert.Equal("Proveedor no entrego el pedido", data.Context.CancellationReason);
        Assert.Equal(new DateTime(2026, 10, 18, 11, 30, 0), data.Context.CancelledAtVenezuela);
    }

    [Fact]
    public async Task GetVoucherDataAsync_CancelledWithSupervisor_ResolvesCancellationAuthorizerName()
    {
        using var stack = new VoucherStack();
        var expense = await SeedExpenseAsync(
            stack.Context,
            status: PettyCashStatus.Cancelled,
            cancelledByUserId: ManagerUserId,
            cancellationAuthorizedByUserId: 1,
            cancellationReason: "Proveedor no entrego el pedido");

        var data = await stack.Service.GetVoucherDataAsync(expense.Id);

        Assert.NotNull(data);
        Assert.Equal("Gerente Dos", data!.Context.CancelledByName);
        Assert.Equal("Administrador", data.Context.CancellationAuthorizedName);
    }

    // ------------------------------------------------------------------ endpoint on-demand (ownership)

    [Fact]
    public async Task GetVoucherReceipt_ForCreatorCashier_ReturnsPdfNamedByVoucherCode()
    {
        using var stack = new VoucherStack();
        var expense = await SeedExpenseAsync(stack.Context);
        var controller = CreateController(stack, userId: CashierUserId.ToString(), role: UserRole.Cashier);

        var result = await controller.GetVoucherReceiptAsync(expense.Id, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("VCC-000001.pdf", file.FileDownloadName);
        Assert.NotEmpty(file.FileContents!);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(file.FileContents!.AsSpan(0, 5).ToArray()));
    }

    [Fact]
    public async Task GetVoucherReceipt_ForOtherCashier_ReturnsForbidden()
    {
        using var stack = new VoucherStack();
        var expense = await SeedExpenseAsync(stack.Context);
        var controller = CreateController(stack, userId: "71", role: UserRole.Cashier);

        var result = await controller.GetVoucherReceiptAsync(expense.Id, CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task GetVoucherReceipt_ForElevatedManager_ReturnsPdf()
    {
        using var stack = new VoucherStack();
        var expense = await SeedExpenseAsync(stack.Context);
        var controller = CreateController(stack, userId: ManagerUserId.ToString(), role: UserRole.Manager);

        var result = await controller.GetVoucherReceiptAsync(expense.Id, CancellationToken.None);

        Assert.IsType<FileContentResult>(result);
    }

    [Fact]
    public async Task GetVoucherReceipt_ForMissingExpense_ReturnsNotFound()
    {
        using var stack = new VoucherStack();
        await SeedUsersAsync(stack.Context);
        var controller = CreateController(stack, userId: "1", role: UserRole.Admin);

        var result = await controller.GetVoucherReceiptAsync(9999, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
    }
}
