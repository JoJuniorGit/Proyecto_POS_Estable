using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;
using Xunit.Abstractions;

namespace CommandCenter.Tests.Unit;

public class ClosureDtoBoundaryTests
{
    private const string GoldenClosureJson =
        "{\"id\":7,\"closureDate\":\"2026-09-17T12:30:00Z\",\"userId\":\"Admin\",\"exchangeRate\":50,\"totalExpectedBsS\":3000,\"totalActualBsS\":3030,\"totalDifferenceBsS\":30,\"observation\":\"Cierre Normal\",\"details\":[{\"id\":11,\"dailyClosureId\":7,\"paymentMethodId\":1,\"paymentMethodName\":\"Efectivo USD\",\"expectedAmountBsS\":1000,\"actualAmountBsS\":1050,\"differenceBsS\":50},{\"id\":12,\"dailyClosureId\":7,\"paymentMethodId\":3,\"paymentMethodName\":\"Punto de Venta\",\"expectedAmountBsS\":2000,\"actualAmountBsS\":1980,\"differenceBsS\":-20}]}";

    private static readonly string[] BoundScalarNames =
    {
        "id", "closureDate", "userId", "exchangeRate",
        "totalExpectedBsS", "totalActualBsS", "totalDifferenceBsS", "observation"
    };

    private static readonly string[] BoundDetailNames =
    {
        "id", "paymentMethodId", "paymentMethodName",
        "expectedAmountBsS", "actualAmountBsS", "differenceBsS"
    };

    private static readonly JsonSerializerOptions ApiJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    private readonly ITestOutputHelper _output;

    public ClosureDtoBoundaryTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static DailyClosure CreateClosureEntity()
    {
        return new DailyClosure
        {
            Id = 7,
            ClosureDate = new DateTime(2026, 9, 17, 12, 30, 0, DateTimeKind.Utc),
            UserId = "Admin",
            ExchangeRate = 50m,
            TotalExpectedBsS = 3000m,
            TotalActualBsS = 3030m,
            TotalDifferenceBsS = 30m,
            Observation = "Cierre Normal",
            Details = new List<ClosureDetail>
            {
                new()
                {
                    Id = 11,
                    DailyClosureId = 7,
                    PaymentMethodId = 1,
                    PaymentMethodName = "Efectivo USD",
                    ExpectedAmountBsS = 1000m,
                    ActualAmountBsS = 1050m,
                    DifferenceBsS = 50m
                },
                new()
                {
                    Id = 12,
                    DailyClosureId = 7,
                    PaymentMethodId = 3,
                    PaymentMethodName = "Punto de Venta",
                    ExpectedAmountBsS = 2000m,
                    ActualAmountBsS = 1980m,
                    DifferenceBsS = -20m
                }
            }
        };
    }

    [Fact]
    public void ClosureDto_GoldenJson_PreservesLegacyBoundFields_AndDropsEntityNavigationMembers()
    {
        var entity = CreateClosureEntity();

        string legacyJson = JsonSerializer.Serialize(entity, ApiJsonOptions);
        string dtoJson = JsonSerializer.Serialize(ShiftReportMapper.MapClosure(entity), ApiJsonOptions);

        _output.WriteLine($"legacy GET closure body: {legacyJson}");
        _output.WriteLine($"S4a GET closure body  : {dtoJson}");

        Assert.Equal(GoldenClosureJson, dtoJson);

        var legacy = JsonNode.Parse(legacyJson)!.AsObject();
        var dto = JsonNode.Parse(dtoJson)!.AsObject();

        foreach (string name in BoundScalarNames)
        {
            Assert.True(legacy.ContainsKey(name), $"legacy body lost '{name}'");
            Assert.True(dto.ContainsKey(name), $"DTO body lost '{name}'");
            Assert.Equal(legacy[name]!.ToJsonString(), dto[name]!.ToJsonString());
        }

        var legacyDetails = legacy["details"]!.AsArray();
        var dtoDetails = dto["details"]!.AsArray();
        Assert.Equal(legacyDetails.Count, dtoDetails.Count);

        for (int i = 0; i < legacyDetails.Count; i++)
        {
            var legacyDetail = legacyDetails[i]!.AsObject();
            var dtoDetail = dtoDetails[i]!.AsObject();
            foreach (string name in BoundDetailNames)
            {
                Assert.True(legacyDetail.ContainsKey(name), $"legacy body lost detail field '{name}'");
                Assert.True(dtoDetail.ContainsKey(name), $"DTO body lost detail field '{name}'");
                Assert.Equal(legacyDetail[name]!.ToJsonString(), dtoDetail[name]!.ToJsonString());
            }
            Assert.True(legacyDetail.ContainsKey("dailyClosure"));
            Assert.True(legacyDetail.ContainsKey("paymentMethod"));
            Assert.False(dtoDetail.ContainsKey("dailyClosure"));
            Assert.False(dtoDetail.ContainsKey("paymentMethod"));
        }

        Assert.DoesNotContain("dailyClosure", PropertyNames(dto));
        Assert.DoesNotContain("paymentMethod", PropertyNames(dto));
    }

    [Fact]
    public void ClosureDtos_ExposeNoPublicSetter()
    {
        AssertNoPublicSetter(typeof(DailyClosureResponseDto));
        AssertNoPublicSetter(typeof(ClosureDetailResponseDto));
    }

    [Fact]
    public void ClosureServiceAndControllerSignatures_DoNotExposeSalesModuleEntities()
    {
        var offenders = new List<string>();

        foreach (var method in typeof(IDailyClosureService).GetMethods())
        {
            CollectEntityTypes(method.ReturnType, $"{nameof(IDailyClosureService)}.{method.Name} (return)", offenders);
            foreach (var parameter in method.GetParameters())
            {
                CollectEntityTypes(parameter.ParameterType, $"{nameof(IDailyClosureService)}.{method.Name} ({parameter.Name})", offenders);
            }
        }

        const BindingFlags publicInstance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var method in typeof(DailyClosureController).GetMethods(publicInstance))
        {
            CollectEntityTypes(method.ReturnType, $"{nameof(DailyClosureController)}.{method.Name} (return)", offenders);
            foreach (var parameter in method.GetParameters())
            {
                CollectEntityTypes(parameter.ParameterType, $"{nameof(DailyClosureController)}.{method.Name} ({parameter.Name})", offenders);
            }
        }

        Assert.Empty(offenders);

        var getClosureReturn = typeof(IDailyClosureService).GetMethod(nameof(IDailyClosureService.GetClosureAsync))!
            .ReturnType.GetGenericArguments().Single();
        Assert.Equal(typeof(DailyClosureResponseDto), getClosureReturn);

        var latestReturn = typeof(IDailyClosureService).GetMethod(nameof(IDailyClosureService.GetLatestClosureAsync))!
            .ReturnType.GetGenericArguments().Single();
        Assert.Equal(typeof(DailyClosureResponseDto), latestReturn);
    }

    [Fact]
    public void ClosureResponseDtos_AreDeclaredInSalesModuleDtosNamespace()
    {
        Assert.Equal("Sales.Module.DTOs", typeof(DailyClosureResponseDto).Namespace);
        Assert.Equal("Sales.Module.DTOs", typeof(ClosureDetailResponseDto).Namespace);
        Assert.True(typeof(DailyClosureResponseDto).IsSealed);
        Assert.True(typeof(ClosureDetailResponseDto).IsSealed);
    }

    [Fact]
    public async Task GetClosure_ReturnsDeclaredDto_AndPreservesNotFoundSemantics()
    {
        var dto = ShiftReportMapper.MapClosure(CreateClosureEntity());
        var service = new Mock<IDailyClosureService>();
        service.Setup(s => s.GetClosureAsync(7)).ReturnsAsync(dto);
        service.Setup(s => s.GetClosureAsync(404)).ReturnsAsync((DailyClosureResponseDto?)null);

        var controller = new DailyClosureController(service.Object, new Mock<ICurrentUserService>().Object);

        var found = await controller.GetClosure(7);
        var ok = Assert.IsType<OkObjectResult>(found.Result);
        var body = Assert.IsType<DailyClosureResponseDto>(ok.Value);
        Assert.Equal(7, body.Id);
        Assert.Equal(2, body.Details.Count);

        string serialized = JsonSerializer.Serialize(ok.Value, ApiJsonOptions);
        var bodyNode = JsonNode.Parse(serialized)!;
        Assert.DoesNotContain("dailyClosure", PropertyNames(bodyNode));
        Assert.DoesNotContain("paymentMethod", PropertyNames(bodyNode));

        var missing = await controller.GetClosure(404);
        Assert.IsType<NotFoundResult>(missing.Result);
    }

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
            if (candidate.Namespace == "Sales.Module.Entities")
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
