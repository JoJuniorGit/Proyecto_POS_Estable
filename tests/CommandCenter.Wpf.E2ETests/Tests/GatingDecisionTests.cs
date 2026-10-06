using System;
using CommandCenter.Wpf.E2ETests.Fixtures;
using Xunit;

namespace CommandCenter.Wpf.E2ETests.Tests;

/// <summary>
/// Política de gating del harness full-stack, sin tocar PostgreSQL: la variable de entorno con la
/// cadena base decide si el suite procede, retorna en silencio (local) o falla cerrado (CI).
/// </summary>
public class GatingDecisionTests
{
    [Fact]
    public void Evaluate_WithConnectionString_ReturnsProceed()
    {
        var decision = FullStackGating.Evaluate("Host=localhost;Database=postgres;Username=postgres;Password=x", null);

        Assert.Equal(FullStackGatingDecision.Proceed, decision);
    }

    [Fact]
    public void Evaluate_WithoutConnectionStringLocally_ReturnsUnavailable()
    {
        var decision = FullStackGating.Evaluate(null, null);

        Assert.Equal(FullStackGatingDecision.Unavailable, decision);
    }

    [Fact]
    public void Evaluate_WithoutConnectionStringInCi_ThrowsFailClosed()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => FullStackGating.Evaluate("   ", "true"));

        Assert.Contains(FullStackGating.ConnectionStringEnvironmentVariable, exception.Message);
        Assert.Contains(FullStackGating.GitHubActionsEnvironmentVariable, exception.Message);
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("True")]
    public void Evaluate_CiMarkerIsCaseInsensitive(string ciMarker)
    {
        Assert.Throws<InvalidOperationException>(() => FullStackGating.Evaluate(null, ciMarker));
    }
}
