using System.Reflection;
using System.Xml.Linq;
using NetArchTest.Rules;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Commits;
using ReviewForge.Core.Editing;
using ReviewForge.Core.Resolve.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Infrastructure.Persistence;
using ReviewForge.Testing;
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
    public void Domain_does_not_depend_on_features()
        => AssertRule(Types.InAssembly(Core)
            .That().ResideInNamespace("ReviewForge.Core.Domain")
            .ShouldNot().HaveDependencyOnAny("ReviewForge.Core.AutoFix", "ReviewForge.Core.Pipeline", "ReviewForge.Core.Resolve")
            .GetResult());


    [Fact]
    public void LoopGuard_lives_in_domain()
        => Assert.Equal("ReviewForge.Core.Domain", typeof(LoopGuard).Namespace);

    [Fact]
    public void ConventionalCommitBuilder_is_not_owned_by_auto_fix()
        => Assert.Equal("ReviewForge.Core.Commits", typeof(ConventionalCommitBuilder).Namespace);

    [Fact]
    public void LineEdit_couple_lives_together()
    {
        Assert.Equal("ReviewForge.Core.Editing", typeof(HashLineEditor).Namespace);
        Assert.Equal("ReviewForge.Core.Editing", typeof(LineEditEngine).Namespace);
    }


    [Fact]
    public void Resolve_is_a_self_contained_feature()
        => AssertRule(Types.InAssembly(typeof(ResolveGate).Assembly)
            .That().ResideInNamespaceStartingWith("ReviewForge.Core.Resolve")
            .ShouldNot().HaveDependencyOnAny("ReviewForge.Core.AutoFix", "ReviewForge.Core.Pipeline.Stages")
            .GetResult());

    [Fact]
    public void AutoFix_does_not_depend_on_Resolve()
        => AssertRule(Types.InAssembly(Core)
            .That().ResideInNamespaceStartingWith("ReviewForge.Core.AutoFix")
            .ShouldNot().HaveDependencyOn("ReviewForge.Core.Resolve")
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
            .Where(type => type.Namespace is { } ns
                           && ns.StartsWith("ReviewForge.Core.Ports", StringComparison.Ordinal)
                           && !type.IsNested)
            .ToArray();
        var approvedValueTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "ChatTier", "DiffBudget", "EnqueueTrigger",
            "PushedFix", "ReviewRequest", "QueueRunSnapshot", "EnqueueResult", "TipCommitInfo", "ProcessRunResult",
            // Bounded scalar projections returned by the global history port (no adapter entities).
            "RunHistoryItem", "RunHistoryPage",
        };

        var unexpected = types
            .Where(type => !type.IsInterface && !approvedValueTypes.Contains(type.Name))
            .Select(type => type.FullName ?? type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.True(unexpected.Length == 0, $"Unexpected Ports types: {string.Join(", ", unexpected)}");

        AssertRule(Types.InAssembly(Core)
            .That().ResideInNamespaceStartingWith("ReviewForge.Core.Ports")
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
        var testing = typeof(FakeGitOps).Assembly;
        Assert.DoesNotContain(testing.GetReferencedAssemblies(), reference =>
            reference.Name is "ReviewForge.Infrastructure" or "ReviewForge.Service" or "ReviewForge.Cli" or "ReviewForge.AppHost");
    }

    [Fact]
    public void Hosts_are_the_only_assemblies_allowed_to_depend_on_service_or_cli()
    {
        var nonHosts = new[] {Core, Infrastructure, Service, typeof(FakeGitOps).Assembly};
        foreach (var assembly in nonHosts)
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                reference.Name is "ReviewForge.Service" or "ReviewForge.Cli");
        }

        var appHostProject = XDocument.Load(Path.Combine(RepoRoot, "src", "ReviewForge.AppHost", "ReviewForge.AppHost.csproj"));
        var references = appHostProject.Descendants("ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension((string?) element.Attribute("Include") ?? string.Empty))
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
                .Select(element => Path.GetFileNameWithoutExtension((string?) element.Attribute("Include") ?? string.Empty))
                .Where(name => name.EndsWith(".Tests", StringComparison.Ordinal))
                .ToArray();

            Assert.Empty(references);
        }
    }

    [Fact]
    public void Cli_and_aspire_share_the_services_https_origin()
    {
        var cli = File.ReadAllText(Path.Combine(RepoRoot, "src", "ReviewForge.Cli", "Program.cs"));
        Assert.Contains("DefaultValueFactory = _ => \"https://localhost:9090\"", cli);
        var appHost = File.ReadAllText(Path.Combine(RepoRoot, "src", "ReviewForge.AppHost", "AppHost.cs"));
        Assert.Contains(".WithHttpsEndpoint(", appHost);
        Assert.Contains("port: 9090", appHost);
        Assert.Contains("targetPort: 9090", appHost);
        Assert.Contains("isProxied: false", appHost);
        Assert.DoesNotContain(".WithHttpEndpoint(", appHost);
    }

    [Fact]
    public void Container_exposes_only_https_and_checks_health_with_certificate_validation()
    {
        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot, "Dockerfile"));
        Assert.Contains("Urls=https://+:9090", dockerfile);
        Assert.Contains("EXPOSE 9090", dockerfile);
        Assert.Contains("--cacert /https/ca.pem", dockerfile);
        Assert.Contains("https://localhost:9090/health", dockerfile);
        Assert.DoesNotContain("--insecure", dockerfile);
        Assert.DoesNotContain("--no-check-certificate", dockerfile);
        Assert.DoesNotContain("8080", dockerfile);
        var compose = File.ReadAllText(Path.Combine(RepoRoot, "docker-compose.yml"));
        Assert.Contains("9090:9090", compose);
        Assert.Contains("Kestrel__Certificates__Default__Password: ${REVIEWFORGE_TLS_CERT_PASSWORD:?", compose);
        Assert.Contains("source: ${REVIEWFORGE_TLS_CERT_PATH:?", compose);
        Assert.Contains("source: ${REVIEWFORGE_TLS_CA_PATH:?", compose);
        Assert.Contains("target: /https/reviewforge.pfx", compose);
        Assert.Contains("target: /https/ca.pem", compose);
        Assert.Contains("create_host_path: false", compose);
        Assert.Contains("read_only: true", compose);
    }

    private static void AssertRule(NetArchTest.Rules.TestResult result)
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