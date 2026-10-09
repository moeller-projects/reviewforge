using Xunit;

namespace ReviewForge.Infrastructure.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentVariableCollection
{
    public const string Name = "Environment variable tests";
}
