using Xunit;

namespace CommandCenter.Tests;

/// <summary>
/// Serializa los tests que mutan o dependen del estado global <c>CurrencyDisplay</c>.
/// El resto de la suite (servicios puros, InMemory, HTTP mocks) sigue en paralelo.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CurrencyDisplayStateCollection
{
    public const string Name = "CurrencyDisplayState";
}
