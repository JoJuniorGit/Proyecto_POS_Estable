using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// Serializa el harness full-stack que abre un puerto Kestrel real, manipula el settings
/// del cliente y puede lanzar la UI. No comparte recursos con los tests unitarios.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FullStackSerialCollection
{
    public const string Name = "FullStackSerial";
}
