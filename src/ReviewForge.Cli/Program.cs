using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;

// Thin client for the reviewforge service — all logic lives server-side.
[ExcludeFromCodeCoverage]
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var serviceUrl = new Option<string>("--service-url")
        {
            Description = "reviewforge service base URL",
            DefaultValueFactory = _ => "http://localhost:5080"
        };

        var org = new Option<string>("--org", "Azure DevOps organization")
        {
            Required = true
        };

        var project = new Option<string>("--project", "ADO project")
        {
            Required = true
        };

        var repo = new Option<string>("--repo", "repository id or name")
        {
            Required = true
        };

        var pr = new Option<int>("--pr", "pull request id")
        {
            Required = true
        };

        var runId = new Option<Guid>("--run-id", "run id returned by submit")
        {
            Required = true
        };

        var apiKey = new Option<string?>("--api-key")
        {
            Description = "API key for the reviewforge service (defaults to REVIEWFORGE_API_KEY)"
        };

        var root = new RootCommand("reviewforge — automated PR review client");

        var submit = new Command("submit", "Enqueue a review run")
        {
            serviceUrl,
            org,
            project,
            repo,
            pr,
            apiKey
        };

        submit.SetAction(async (parseResult, cancellationToken) =>
        {
            var url = parseResult.GetValue(serviceUrl)
                      ?? throw new InvalidOperationException("--service-url must have a value");
            var organization = parseResult.GetValue(org);
            var projectName = parseResult.GetValue(project);
            var repository = parseResult.GetValue(repo);
            var pullRequestId = parseResult.GetValue(pr);
            var key = parseResult.GetValue(apiKey) ?? Environment.GetEnvironmentVariable("REVIEWFORGE_API_KEY");

            using var http = new HttpClient
            {
                BaseAddress = new Uri(url)
            };
            if (!string.IsNullOrEmpty(key))
            {
                http.DefaultRequestHeaders.Add("X-Api-Key", key);
            }

            var response = await http.PostAsJsonAsync(
                "/reviews",
                new
                {
                    org = organization,
                    project = projectName,
                    repositoryId = repository,
                    prId = pullRequestId
                },
                cancellationToken);

            response.EnsureSuccessStatusCode();
            Console.WriteLine(
                await response.Content.ReadAsStringAsync(cancellationToken));

            return 0;
        });

        var status = new Command("status", "Get run status")
        {
            serviceUrl,
            runId,
            apiKey
        };

        status.SetAction(async (parseResult, cancellationToken) =>
        {
            var url = parseResult.GetValue(serviceUrl)
                      ?? throw new InvalidOperationException("--service-url must have a value");
            var reviewRunId = parseResult.GetValue(runId);
            var key = parseResult.GetValue(apiKey) ?? Environment.GetEnvironmentVariable("REVIEWFORGE_API_KEY");

            using var http = new HttpClient
            {
                BaseAddress = new Uri(url)
            };
            if (!string.IsNullOrEmpty(key))
            {
                http.DefaultRequestHeaders.Add("X-Api-Key", key);
            }

            using var response = await http.GetAsync(
                $"/reviews/{reviewRunId}",
                cancellationToken);

            Console.WriteLine(
                await response.Content.ReadAsStringAsync(cancellationToken));

            return response.IsSuccessStatusCode ? 0 : 1;
        });

        root.Subcommands.Add(submit);
        root.Subcommands.Add(status);

        return await root.Parse(args).InvokeAsync();
    }
}