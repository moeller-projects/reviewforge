using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ReviewForge.Service.Tests;

[Collection("ReviewForge service host")]
public sealed class SubmitReviewContractTests
{
    [Fact]
    public async Task Accepted_submit_returns_stable_run_id_and_polling_url_contract()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/reviews", new
        {
            org = "org",
            project = "project",
            repositoryId = "repository",
            prId = 42,
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement;
        Assert.Equal(new[] { "runId", "statusUrl" }, body.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray());

        var runId = body.GetProperty("runId").GetGuid();
        var statusUrl = $"/reviews/{runId}";
        Assert.Equal(statusUrl, body.GetProperty("statusUrl").GetString());
        Assert.Equal(statusUrl, response.Headers.Location!.OriginalString);
    }
}
