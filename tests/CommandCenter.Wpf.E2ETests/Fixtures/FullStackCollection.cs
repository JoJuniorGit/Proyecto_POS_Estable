using Xunit;

namespace CommandCenter.Wpf.E2ETests.Fixtures;

/// <summary>
/// Serializa las clases full-stack. Cada una levanta su propio Backend.API en un puerto libre y
/// un Desktop.Client real; en paralelo dos fixtures pueden elegir el mismo puerto (carrera entre
/// <see cref="FullStackFixture.FindFreePort"/> y el bind del backend) y las ventanas compiten por
/// foco y UI Automation. Las pruebas mock no usan esta colección y conservan su paralelismo.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FullStackCollection
{
    public const string Name = "FullStackE2E";
}
