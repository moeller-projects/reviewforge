using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Profiling;
using Xunit;

namespace ReviewForge.Service.Tests;

[Collection("ReviewForge service host")]
public class ModelProfileEndpointTests
{
    [Theory]
    [InlineData(RunKind.Review)]
    [InlineData(RunKind.ReviewDraft)]
    [InlineData(RunKind.Resolve)]
    public async Task Model_calls_are_authenticated_terminal_only_and_paginated(RunKind kind)
    {
        await using var factory = new ReviewForgeFactory();
        using var client = factory.CreateClient();
        var tracker = factory.Services.GetRequiredService<RunTracker>();
        var key = new PrKey("o", "p", "r", 1);
        var id = Guid.NewGuid();
        tracker.Set(id, key, RunState.Running, kind: kind);
        factory.Store.ThrowOnModelProfileRead = true;
        var path = $"/reviews/{id}/model-calls?after=0&count=1";
        var page = await client.GetFromJsonAsync<ModelProfilePage>(path, TestContext.Current.CancellationToken);
        Assert.Equal("not-yet-available", page!.State);
        factory.Store.ThrowOnModelProfileRead = false;
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/reviews/{Guid.NewGuid()}/model-calls", TestContext.Current.CancellationToken)).StatusCode);
        var collector = new ModelProfileCollector(id);
        collector.Begin("Triage", ModelCallProfile.Triage, "openai", "gpt-5").Complete("completed", null);
        await factory.Services.GetRequiredService<IModelProfileStore>().FinalizeModelProfileAsync(collector.Snapshot(),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), false, TestContext.Current.CancellationToken);
        tracker.Set(id, key, RunState.Completed, kind: kind);
        page = await client.GetFromJsonAsync<ModelProfilePage>(path, TestContext.Current.CancellationToken);
        Assert.Single(page!.Calls);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/reviews/{id}/model-calls?after=-1&count=101", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Empty(factory.Source.PostedFindings);
        var skipped = Guid.NewGuid();
        tracker.Set(skipped, key, RunState.Skipped, kind: kind);
        Assert.Equal("skipped-not-recorded", (await client.GetFromJsonAsync<ModelProfilePage>($"/reviews/{skipped}/model-calls", TestContext.Current.CancellationToken))!.State);
        client.DefaultRequestHeaders.Clear();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
    }
}
