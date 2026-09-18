using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Backend.API.Controllers;
using CommandCenter.Tests.Builders;
using Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;
using Xunit.Abstractions;

namespace CommandCenter.Tests.Unit;

public class DrawerDtoBoundaryTests
{
    private const string GoldenSessionJson =
        "{\"id\":3,\"openedAt\":\"2026-09-17T12:30:00Z\",\"openedAtLocal\":\"0001-01-01T00:00:00\",\"closedAt\":null,\"closedAtLocal\":null,\"status\":0,\"openingBalanceLocal\":1000,\"openingExchangeRate\":50,\"closingBalanceLocal\":null,\"closingExchangeRate\":null,\"transactions\":[{\"id\":31,\"sessionId\":3,\"transactionTime\":\"2026-09-17T12:35:00Z\",\"transactionTimeLocal\":\"0001-01-01T00:00:00\",\"type\":0,\"source\":1,\"amountUsd\":10,\"exchangeRate\":50,\"amountLocal\":500,\"description\":\"Pago en efectivo\",\"referenceId\":null,\"saleId\":700,\"invoiceNumber\":4242,\"isPhysicalCash\":true,\"paymentMethodId\":1}]}";

    private static readonly string[] BoundSessionScalarNames =
    {
        "id", "openedAt", "openedAtLocal", "closedAt", "closedAtLocal", "status",
        "openingBalanceLocal", "openingExchangeRate", "closingBalanceLocal", "closingExchangeRate"
    };

    private static readonly string[] BoundTransactionNames =
    {
        "id", "sessionId", "transactionTime", "transactionTimeLocal", "type", "source",
        "amountUsd", "exchangeRate", "amountLocal", "description", "referenceId", "saleId",
        "invoiceNumber", "isPhysicalCash", "paymentMethodId"
    };

    private static readonly JsonSerializerOptions ApiJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    private readonly ITestOutputHelper _output;

    public DrawerDtoBoundaryTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static async Task<Sales.Module.Data.SalesDbContext> SeedDrawerAsync()
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();

        context.Sales.Add(new Sale
        {
            Id = 700,
            Status = SaleStatus.Completed,
            InvoiceNumber = 4242,
            Date = new DateTime(2026, 9, 17, 12, 34, 0, DateTimeKind.Utc),
            AppliedRate = 50m
        });

        context.CashDrawerSessions.Add(new CashDrawerSession
        {
            Id = 3,
            OpenedAt = new DateTime(2026, 9, 17, 12, 30, 0, DateTimeKind.Utc),
            OpeningBalanceLocal = 1000m,
            OpeningExchangeRate = 50m,
            Status = CashDrawerStatus.Open
        });

        context.CashTransactions.Add(new CashTransaction
        {
            Id = 31,
            SessionId = 3,
            SaleId = 700,
            TransactionTime = new DateTime(2026, 9, 17, 12, 35, 0, DateTimeKind.Utc),
            Type = CashTransactionType.Income,
            Source = CashTransactionSource.SalePayment,
            AmountUsd = 10m,
            ExchangeRate = 50m,
            AmountLocal = 500m,
            Description = "Pago en efectivo",
            IsPhysicalCash = true,
            PaymentMethodId = 1
        });

        context.CashTransactions.Add(new CashTransaction
        {
            Id = 32,
            SessionId = 3,
            TransactionTime = new DateTime(2026, 9, 17, 12, 36, 0, DateTimeKind.Utc),
            Type = CashTransactionType.Income,
            Source = CashTransactionSource.SalePayment,
            AmountUsd = 2m,
            ExchangeRate = 50m,
            AmountLocal = 100m,
            Description = "Pago Movil",
            IsPhysicalCash = false,
            PaymentMethodId = 2
        });

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return context;
    }

    [Fact]
    public async Task DrawerSessionDto_GoldenJson_PreservesLegacyBoundFields_AndDropsEntityNavigationMembers()
    {
        using var context = await SeedDrawerAsync();

        var legacyEntity = await context.CashDrawerSessions
            .AsNoTracking()
            .Include(s => s.Transactions)
                .ThenInclude(t => t.Sale)
            .FirstAsync(s => s.Id == 3);

        string legacyJson = JsonSerializer.Serialize(legacyEntity, ApiJsonOptions);
        _output.WriteLine($"legacy GET active-session body: {legacyJson}");

        var service = new CashDrawerService(context);
        var dto = await service.GetActiveSessionWithTransactionsAsync();

        string dtoJson = JsonSerializer.Serialize(dto, ApiJsonOptions);
        _output.WriteLine($"S4b GET active-session body  : {dtoJson}");

        Assert.Equal(GoldenSessionJson, dtoJson);

        var legacy = JsonNode.Parse(legacyJson)!.AsObject();
        var body = JsonNode.Parse(dtoJson)!.AsObject();

        foreach (string name in BoundSessionScalarNames)
        {
            Assert.True(legacy.ContainsKey(name), $"legacy body lost '{name}'");
            Assert.True(body.ContainsKey(name), $"DTO body lost '{name}'");
            Assert.Equal(JsonToken(legacy, name), JsonToken(body, name));
        }

        var legacyTransactions = legacy["transactions"]!.AsArray();
        var dtoTransactions = body["transactions"]!.AsArray();
        var legacyPhysical = legacyTransactions
            .Select(t => t!.AsObject())
            .Single(t => t["id"]!.GetValue<int>() == 31);
        var dtoTransaction = Assert.Single(dtoTransactions)!.AsObject();

        foreach (string name in BoundTransactionNames)
        {
            Assert.True(dtoTransaction.ContainsKey(name), $"DTO body lost transaction field '{name}'");
        }

        Assert.Equal(legacyPhysical["type"]!.ToJsonString(), dtoTransaction["type"]!.ToJsonString());
        Assert.Equal(legacyPhysical["source"]!.ToJsonString(), dtoTransaction["source"]!.ToJsonString());
        Assert.Equal(legacyPhysical["amountUsd"]!.ToJsonString(), dtoTransaction["amountUsd"]!.ToJsonString());
        Assert.Equal(legacyPhysical["amountLocal"]!.ToJsonString(), dtoTransaction["amountLocal"]!.ToJsonString());
        Assert.Equal(legacyPhysical["exchangeRate"]!.ToJsonString(), dtoTransaction["exchangeRate"]!.ToJsonString());
        Assert.Equal(legacyPhysical["description"]!.ToJsonString(), dtoTransaction["description"]!.ToJsonString());
        Assert.Equal(legacyPhysical["sale"]!["invoiceNumber"]!.ToJsonString(), dtoTransaction["invoiceNumber"]!.ToJsonString());

        Assert.True(legacyPhysical.ContainsKey("session"));
        Assert.True(legacyPhysical.ContainsKey("sale"));
        Assert.True(legacyPhysical.ContainsKey("paymentMethod"));
        Assert.False(dtoTransaction.ContainsKey("session"));
        Assert.False(dtoTransaction.ContainsKey("sale"));
        Assert.False(dtoTransaction.ContainsKey("paymentMethod"));

        var propertyNames = PropertyNames(body);
        Assert.DoesNotContain("session", propertyNames);
        Assert.DoesNotContain("sale", propertyNames);
        Assert.DoesNotContain("paymentMethod", propertyNames);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsProjectedDtos_WithoutMaterializingEntityInstances()
    {
        using var context = await SeedDrawerAsync();

        var service = new CashDrawerService(context);
        var history = await service.GetHistoryAsync(10);

        var item = Assert.Single(history, t => t.SaleId == 700);
        Assert.Equal(typeof(CashTransactionResponseDto), item.GetType());
        Assert.Equal(4242, item.InvoiceNumber);
        Assert.IsType<string>(item.Description);
        Assert.DoesNotContain(history, t => t.Id == 32);

        Assert.Empty(context.ChangeTracker.Entries<CashTransaction>());
    }

    [Fact]
    public void DrawerDtos_ExposeNoPublicSetter()
    {
        AssertNoPublicSetter(typeof(CashDrawerSessionResponseDto));
        AssertNoPublicSetter(typeof(CashTransactionResponseDto));
    }

    [Fact]
    public void DrawerResponseDtos_AreDeclaredInSalesModuleDtosNamespace()
    {
        Assert.Equal("Sales.Module.DTOs", typeof(CashDrawerSessionResponseDto).Namespace);
        Assert.Equal("Sales.Module.DTOs", typeof(CashTransactionResponseDto).Namespace);
        Assert.True(typeof(CashDrawerSessionResponseDto).IsSealed);
        Assert.True(typeof(CashTransactionResponseDto).IsSealed);
    }

    [Fact]
    public void CashAdvanceResultDto_ExposesTransactionResponseDtos()
    {
        Assert.Equal(typeof(CashTransactionResponseDto), typeof(CashAdvanceResultDto).GetProperty(nameof(CashAdvanceResultDto.ExpenseTransaction))!.PropertyType);
        Assert.Equal(typeof(CashTransactionResponseDto), typeof(CashAdvanceResultDto).GetProperty(nameof(CashAdvanceResultDto.IncomeTransaction))!.PropertyType);
    }

    [Fact]
    public void CashDrawerServiceAndControllerSignatures_DoNotExposeSalesModuleEntities()
    {
        var offenders = new List<string>();

        foreach (var method in typeof(ICashDrawerService).GetMethods())
        {
            CollectEntityTypes(method.ReturnType, $"{nameof(ICashDrawerService)}.{method.Name} (return)", offenders);
            foreach (var parameter in method.GetParameters())
            {
                CollectEntityTypes(parameter.ParameterType, $"{nameof(ICashDrawerService)}.{method.Name} ({parameter.Name})", offenders);
            }
        }

        const BindingFlags publicInstance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var method in typeof(CashDrawerController).GetMethods(publicInstance))
        {
            CollectEntityTypes(method.ReturnType, $"{nameof(CashDrawerController)}.{method.Name} (return)", offenders);
            foreach (var parameter in method.GetParameters())
            {
                CollectEntityTypes(parameter.ParameterType, $"{nameof(CashDrawerController)}.{method.Name} ({parameter.Name})", offenders);
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void CashDrawerController_Actions_DeclareDrawerResponseDtos()
    {
        const BindingFlags publicInstance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        Assert.Equal(typeof(CashDrawerSessionResponseDto), ActionBodyType(GetAction(nameof(CashDrawerController.GetActiveSession))));
        Assert.Equal(typeof(CashDrawerSessionResponseDto), ActionBodyType(GetAction(nameof(CashDrawerController.OpenSession))));
        Assert.Equal(typeof(CashDrawerSessionResponseDto), ActionBodyType(GetAction(nameof(CashDrawerController.CloseSession))));
        Assert.Equal(typeof(CashTransactionResponseDto), ActionBodyType(GetAction(nameof(CashDrawerController.AddTransaction))));
        Assert.Equal(typeof(CashAdvanceResultDto), ActionBodyType(GetAction(nameof(CashDrawerController.ProcessCashAdvance))));

        var historyBody = ActionBodyType(GetAction(nameof(CashDrawerController.GetHistory)));
        Assert.Equal(typeof(CashTransactionResponseDto), historyBody.GetGenericArguments().Single());

        static MethodInfo GetAction(string name) => typeof(CashDrawerController)
            .GetMethods(publicInstance)
            .Single(m => m.Name == name);
    }

    [Fact]
    public async Task GetActiveSession_ReturnsDeclaredDto_AndPreservesEmptySessionSemantics()
    {
        using var salesDb = TestDatabaseFactory.CreateSalesDbContext();
        using var inventoryDb = TestDatabaseFactory.CreateInventoryDbContext();

        var dto = new CashDrawerSessionResponseDto
        {
            Id = 3,
            OpenedAt = new DateTime(2026, 9, 17, 12, 30, 0, DateTimeKind.Utc),
            OpeningBalanceLocal = 1000m,
            OpeningExchangeRate = 50m,
            Status = CashDrawerStatus.Open,
            Transactions = new List<CashTransactionResponseDto>
            {
                new()
                {
                    Id = 31,
                    SessionId = 3,
                    TransactionTime = new DateTime(2026, 9, 17, 12, 35, 0, DateTimeKind.Utc),
                    Type = CashTransactionType.Income,
                    Source = CashTransactionSource.SalePayment,
                    AmountLocal = 500m,
                    Description = "Pago en efectivo",
                    IsPhysicalCash = true
                }
            }
        };

        var service = new Mock<ICashDrawerService>();
        service.Setup(s => s.GetActiveSessionWithTransactionsAsync()).ReturnsAsync(dto);

        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        var coordinator = new CashAdvanceCoordinator(salesDb, Mock.Of<ISalesService>(), service.Object, settings.Object);
        var controller = new CashDrawerController(service.Object, settings.Object, salesDb, new Mock<ICurrentUserService>().Object, inventoryDb, coordinator);

        var found = await controller.GetActiveSession();
        var ok = Assert.IsType<OkObjectResult>(found.Result);
        var body = Assert.IsType<CashDrawerSessionResponseDto>(ok.Value);
        Assert.Equal(3, body.Id);
        Assert.Single(body.Transactions);

        string serialized = JsonSerializer.Serialize(ok.Value, ApiJsonOptions);
        var bodyNode = JsonNode.Parse(serialized)!;
        Assert.DoesNotContain("session", PropertyNames(bodyNode));
        Assert.DoesNotContain("sale", PropertyNames(bodyNode));
        Assert.DoesNotContain("paymentMethod", PropertyNames(bodyNode));

        service.Setup(s => s.GetActiveSessionWithTransactionsAsync()).ReturnsAsync((CashDrawerSessionResponseDto?)null);
        var empty = await controller.GetActiveSession();
        var emptyOk = Assert.IsType<OkObjectResult>(empty.Result);
        Assert.Null(emptyOk.Value);
    }

    private static Type ActionBodyType(MethodInfo method)
    {
        var type = method.ReturnType;

        while (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Task<>) || definition == typeof(ActionResult<>))
            {
                type = type.GetGenericArguments()[0];
                continue;
            }
            break;
        }

        return type;
    }

    private static string JsonToken(JsonObject source, string name)
        => source[name]?.ToJsonString() ?? "null";

    private static List<string> PropertyNames(JsonNode node)
    {
        var names = new List<string>();

        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                names.Add(pair.Key);
                if (pair.Value is not null)
                {
                    names.AddRange(PropertyNames(pair.Value));
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    names.AddRange(PropertyNames(item));
                }
            }
        }

        return names;
    }

    private static void AssertNoPublicSetter(Type dtoType)
    {
        foreach (var property in dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var setter = property.SetMethod;
            if (setter is null)
            {
                continue;
            }

            bool isInitOnly = setter.ReturnParameter
                .GetRequiredCustomModifiers()
                .Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");

            Assert.True(isInitOnly,
                $"{dtoType.Name}.{property.Name} exposes a mutable public setter instead of init-only semantics");
        }
    }

    private static void CollectEntityTypes(Type type, string owner, List<string> offenders)
    {
        foreach (var candidate in Flatten(type))
        {
            if (candidate.Namespace == "Sales.Module.Entities" && candidate.IsClass)
            {
                offenders.Add($"{owner} -> {candidate.FullName}");
            }
        }
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;

        if (type.IsArray && type.GetElementType() is { } elementType)
        {
            foreach (var nested in Flatten(elementType))
            {
                yield return nested;
            }
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var nested in Flatten(argument))
                {
                    yield return nested;
                }
            }
        }
    }
}
