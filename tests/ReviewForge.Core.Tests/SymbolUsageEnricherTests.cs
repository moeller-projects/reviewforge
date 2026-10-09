using ReviewForge.Core.Reasoning;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class SymbolUsageEnricherTests : IDisposable
{
    private readonly string _Root = Path.Combine(Path.GetTempPath(), "reviewforge-symbols-" + Guid.NewGuid().ToString("N"));

    public SymbolUsageEnricherTests() => Directory.CreateDirectory(_Root);

    [Fact]
    public async Task Builds_deterministic_usage_map_and_excludes_changed_files()
    {
        Directory.CreateDirectory(Path.Combine(_Root, "src"));
        await File.WriteAllTextAsync(Path.Combine(_Root, "src", "Changed.cs"), "ParseConfig();\n");
        await File.WriteAllTextAsync(Path.Combine(_Root, "src", "Consumer.cs"),
            "public class Consumer { void Run() { ParseConfig(); } }\n");
        var diff = """
            diff --git a/src/Changed.cs b/src/Changed.cs
            --- a/src/Changed.cs
            +++ b/src/Changed.cs
            @@ -1 +1 @@
            -old
            +public ParseConfig ParseConfig() { return ParseConfig(); }
            """;

        var result = await new SymbolUsageEnricher().EnrichAsync(_Root, diff, CancellationToken.None);

        Assert.NotNull(result);
        Assert.StartsWith("# Symbol usage map (references outside the PR diff)", result);
        Assert.Contains("ParseConfig — 1 references: src/Consumer.cs:1", result);
        Assert.DoesNotContain("Changed.cs", result);
    }

    [Fact]
    public async Task Reports_no_external_references_and_filters_keywords()
    {
        await File.WriteAllTextAsync(Path.Combine(_Root, "Only.cs"), "class Only { }");
        var diff = """
            diff --git a/Only.cs b/Only.cs
            --- a/Only.cs
            +++ b/Only.cs
            @@ -0,0 +1 @@
            +public const int NewSymbol = 1;
            """;

        var result = await new SymbolUsageEnricher().EnrichAsync(_Root, diff, CancellationToken.None);

        Assert.Contains("NewSymbol — no external references", result);
        Assert.DoesNotContain("public —", result);
        Assert.DoesNotContain("const —", result);
    }

    [Fact]
    public async Task Reports_multiple_symbols_caps_call_sites_and_counts_additional_references()
    {
        await File.WriteAllLinesAsync(Path.Combine(_Root, "Consumer.cs"),
        [
            "ParseConfig();",
            "ParseConfig();",
            "ParseConfig();",
            "ParseConfig();",
            "BuildResult();",
        ]);
        var diff = "+public ParseConfig BuildResult() => new();\n";

        var result = await new SymbolUsageEnricher().EnrichAsync(_Root, diff, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("ParseConfig — 4 references: Consumer.cs:1, Consumer.cs:2, Consumer.cs:3 (+1 more)", result);
        Assert.Contains("BuildResult — 1 references: Consumer.cs:5", result);
    }

    [Fact]
    public async Task Converts_cancellation_or_unexpected_errors_to_null()
    {
        var diff = "+public NewSymbol = 1;\n";
        File.WriteAllText(Path.Combine(_Root, "Cancel.cs"), "class Cancel { }");
        var result = await new SymbolUsageEnricher().EnrichAsync(
            _Root, diff, new CancellationToken(canceled: true));

        Assert.Null(result);
        Assert.Equal(SymbolUsageEnricher.ContextName, new SymbolUsageEnricher().Name);
    }

    [Fact]
    public async Task Fails_safe_for_missing_repository_and_empty_symbols()
    {
        var missing = await new SymbolUsageEnricher().EnrichAsync(
            Path.Combine(_Root, "missing"), "+--- a/file\n+++ b/file\n", CancellationToken.None);
        var empty = await new SymbolUsageEnricher().EnrichAsync(_Root, "diff --git a/a b/a\n", CancellationToken.None);

        Assert.Null(missing);
        Assert.Null(empty);
    }

    public void Dispose()
    {
        try { Directory.Delete(_Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
