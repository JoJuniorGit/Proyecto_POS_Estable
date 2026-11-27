using Xunit;

namespace CommandCenter.Tests;

/// <summary>
/// Serializa los tests que publican mensajes en el bus estático global. Los receptores
/// de otros tests no deben observar mensajes ajenos a mitad de una aserción.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WeakMessengerStateCollection
{
    public const string Name = "WeakMessengerState";
}
