using Xunit;

namespace CommandCenter.Tests.Integration;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresRealCollection
{
    public const string Name = "PostgresRealSharedState";
}
