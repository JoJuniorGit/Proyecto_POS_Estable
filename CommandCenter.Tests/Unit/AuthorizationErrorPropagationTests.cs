using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.150 (T10, design D5/D7): el 403 del gate ManualPriceOverride debe ser programaticamente
/// detectable (extensiones ProblemDetails) sin string matching, y el resto del contrato de error
/// de AddItemAsync debe permanecer intacto.
/// </summary>
public class AuthorizationErrorPropagationTests
{
    private const string ForbiddenWithExtensions = """
        {"type":"https://httpstatuses.com/403","title":"Forbidden","status":403,"message":"Se requiere autorización remota de un administrador.","authorizationRequired":true,"authorizationAction":"ManualPriceOverride"}
        """;

    private static SalesService CreateService(HttpStatusCode status, string body)
    {
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        }));
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };
        return new SalesService(client);
    }

    [Fact]
    public void Parser_DetectsCamelCaseAuthorizationExtensions()
    {
        var detected = ApiErrorParser.TryGetAuthorizationRequirement(ForbiddenWithExtensions, out var action);

        Assert.True(detected);
        Assert.Equal("ManualPriceOverride", action);
    }

    [Fact]
    public void Parser_DetectsPascalCaseAuthorizationExtensions()
    {
        var body = """{"message":"Acceso denegado.","AuthorizationRequired":true,"AuthorizationAction":"ManualPriceOverride"}""";

        var detected = ApiErrorParser.TryGetAuthorizationRequirement(body, out var action);

        Assert.True(detected);
        Assert.Equal("ManualPriceOverride", action);
    }

    [Fact]
    public void Parser_ReturnsFalse_WhenExtensionAbsentOrFalse()
    {
        Assert.False(ApiErrorParser.TryGetAuthorizationRequirement(
            """{"message":"Acceso denegado."}""", out var absent));
        Assert.Null(absent);

        Assert.False(ApiErrorParser.TryGetAuthorizationRequirement(
            """{"authorizationRequired":false,"authorizationAction":"ManualPriceOverride"}""", out var disabled));
        Assert.Null(disabled);

        Assert.True(ApiErrorParser.TryGetAuthorizationRequirement(
            """{"authorizationRequired":"true","authorizationAction":"ManualPriceOverride"}""", out var enabled));
        Assert.Equal("ManualPriceOverride", enabled);
    }

    [Fact]
    public void Parser_IsSafeOnNonJsonBodies()
    {
        Assert.False(ApiErrorParser.TryGetAuthorizationRequirement("{not json", out var malformed));
        Assert.Null(malformed);

        Assert.False(ApiErrorParser.TryGetAuthorizationRequirement(null, out var empty));
        Assert.Null(empty);

        Assert.False(ApiErrorParser.TryGetAuthorizationRequirement("[1,2,3]", out var array));
        Assert.Null(array);
    }

    [Fact]
    public async Task AddItemAsync_WithAuthorizationExtensions_ThrowsDedicatedException()
    {
        var service = CreateService(HttpStatusCode.Forbidden, ForbiddenWithExtensions);

        var exception = await Assert.ThrowsAsync<AuthorizationRequiredException>(
            () => service.AddItemAsync(1, 42, 1m, 50m, 10m, 500m));

        Assert.Equal("ManualPriceOverride", exception.AuthorizationAction);
        Assert.Equal("Se requiere autorización remota de un administrador.", exception.Message);
    }

    [Fact]
    public async Task AddItemAsync_WithoutExtensions_KeepsLegacyInvalidOperationException()
    {
        var service = CreateService(
            HttpStatusCode.Forbidden,
            """{"type":"https://httpstatuses.com/403","title":"Forbidden","status":403,"message":"Acceso denegado."}""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddItemAsync(1, 42, 1m, 50m, 10m, 500m));

        Assert.Equal("Acceso denegado.", exception.Message);
    }

    [Fact]
    public async Task AddItemAsync_WithoutMessageProperty_KeepsLegacyFallback()
    {
        var service = CreateService(HttpStatusCode.BadRequest, """{"title":"Bad Request","status":400}""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddItemAsync(1, 42, 1m, 50m, null, null));

        Assert.Equal("Failed to add item.", exception.Message);
    }

    [Fact]
    public async Task AddItemAsync_WithExtensionsOnNonForbiddenStatus_KeepsLegacyPath()
    {
        var service = CreateService(HttpStatusCode.InternalServerError, ForbiddenWithExtensions);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddItemAsync(1, 42, 1m, 50m, 10m, 500m));

        Assert.Equal("Se requiere autorización remota de un administrador.", exception.Message);
    }
}
