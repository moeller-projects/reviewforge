using System.Reflection;
using System.Xml.Linq;
using NetArchTest.Rules;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Persistence;
using ReviewForge.Service;
using Xunit;

namespace ReviewForge.Architecture.Tests;

public sealed class ArchitectureTests
{
    private static readonly Assembly Core = typeof(IReviewStage).Assembly;
    private static readonly Assembly Infrastructure = typeof(SqliteFindingStore).Assembly;
    private static readonly Assembly Service = typeof(Program).Assembly;
    private static readonly string RepoRoot = FindRepositoryRoot();

    [Fact]
    public void Core_does_not_depend_on_adapters_or_hosts()
        => AssertRule(
            Types.InAssembly(Core).ShouldNot().HaveDependencyOnAny(
                "ReviewForge.Infrastructure", "ReviewForge.Service", "ReviewForge.Cli", "ReviewForge.AppHost")
                .GetResult());
    [Fact]
    public void Core_does_not_depend_on_adapter_vendor_technology()
        => AssertRule(
            Types.InAssembly(Core).ShouldNot().HaveDependencyOnAny(
                "LibGit2Sharp", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore",
                "Microsoft.TeamFoundationServer", "Microsoft.VisualStudio.Services", "System.Net.Http")
                .GetResult());

    [Fact]
    public void Core_has_no_direct_system_net_http_reference()
        => Assert.DoesNotContain(Core.GetReferencedAssemblies(), reference => reference.Name == "System.Net.Http");

    [Fact]
    public void Core_reference_allow_list_is_bcl_and_declared_microsoft_packages()
    {
        var unexpected = Core.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => !name.StartsWith("System", StringComparison.Ordinal)
                && !name.Equals("netstandard", StringComparison.Ordinal)
                && !name.StartsWith("Microsoft.Agents.AI", StringComparison.Ordinal)
                && !name.StartsWith("Microsoft.Extensions", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(unexpected.Length == 0, $"Unexpected Core references: {string.Join(", ", unexpected)}");
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_hosts()
        => AssertRule(
            Types.InAssembly(Infrastructure).ShouldNot().HaveDependencyOnAny(
                "ReviewForge.Service", "ReviewForge.Cli", "ReviewForge.AppHost")
                .GetResult());
    [Fact]
    public void Ports_contain_contract_interfaces_and_approved_value_types_only()
    {
        var types = Core.GetTypes()
            .Where(type => type.Namespace == "ReviewForge.Core.Ports" && !type.IsNested)
            .ToArray();
        var approvedValueTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "ChatTier", "DiffBudget", "InstrumentedPullRequestSource",
            "ReviewRequest", "EnqueueResult",
        };

        var unexpected = types
            .Where(type => !type.IsInterface && !approvedValueTypes.Contains(type.Name))
            .Select(type => type.FullName ?? type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.True(unexpected.Length == 0, $"Unexpected Ports types: {string.Join(", ", unexpected)}");

        AssertRule(Types.InAssembly(Core)
            .That().ResideInNamespace("ReviewForge.Core.Ports")
            .ShouldNot().HaveDependencyOnAny(
                "LibGit2Sharp", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore",
                "Microsoft.TeamFoundationServer", "Microsoft.VisualStudio.Services", "System.Net.Http")
            .GetResult());
    }

    [Fact]
    public void Review_stages_do_not_depend_on_adapters_or_vendor_sdks()
    {
        var forbidden = new[]
        {
            "ReviewForge.Infrastructure", "LibGit2Sharp", "Microsoft.EntityFrameworkCore",
            "Microsoft.TeamFoundationServer", "Microsoft.VisualStudio.Services", "Microsoft.AspNetCore",
        };

        AssertRule(Types.InAssembly(Core)
            .That().ImplementInterface(typeof(IReviewStage))
            .ShouldNot().HaveDependencyOnAny(forbidden)
            .GetResult());
        AssertRule(Types.InAssembly(Core)
            .That().ResideInNamespace("ReviewForge.Core.Pipeline.Stages")
            .ShouldNot().HaveDependencyOnAny(forbidden)
            .GetResult());
    }

    [Fact]
    public void Shared_testing_doubles_depend_on_core_only()
    {
        var testing = typeof(ReviewForge.Testing.FakeGitOps).Assembly;
        Assert.DoesNotContain(testing.GetReferencedAssemblies(), reference =>
            reference.Name is "ReviewForge.Infrastructure" or "ReviewForge.Service" or "ReviewForge.Cli" or "ReviewForge.AppHost");
    }

    [Fact]
    public void Hosts_are_the_only_assemblies_allowed_to_depend_on_service_or_cli()
    {
        var nonHosts = new[] {Core, Infrastructure, Service, typeof(ReviewForge.Testing.FakeGitOps).Assembly};
        foreach (var assembly in nonHosts)
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                reference.Name is "ReviewForge.Service" or "ReviewForge.Cli");
        }

        var appHostProject = XDocument.Load(Path.Combine(RepoRoot, "src", "ReviewForge.AppHost", "ReviewForge.AppHost.csproj"));
        var references = appHostProject.Descendants("ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension((string?)element.Attribute("Include") ?? string.Empty))
            .ToArray();
        Assert.Contains("ReviewForge.Service", references);
    }

    [Fact]
    public void Test_projects_do_not_reference_each_other_except_shared_testing()
    {
        var testDirectories = Directory.GetDirectories(Path.Combine(RepoRoot, "tests"), "ReviewForge.*.Tests");
        foreach (var directory in testDirectories)
        {
            var project = XDocument.Load(Directory.GetFiles(directory, "*.csproj").Single());
            var references = project.Descendants("ProjectReference")
                .Select(element => Path.GetFileNameWithoutExtension((string?)element.Attribute("Include") ?? string.Empty))
                .Where(name => name.EndsWith(".Tests", StringComparison.Ordinal))
                .ToArray();

            Assert.Empty(references);
        }
    }

    private static void AssertRule(TestResult result)
    {
        Assert.True(result.IsSuccessful,
            string.Join("; ", result.FailingTypeNames ?? Array.Empty<string>()));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ReviewForge.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
