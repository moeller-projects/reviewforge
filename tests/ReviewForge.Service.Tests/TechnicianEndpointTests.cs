using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using Xunit;

namespace ReviewForge.Service.Tests;

[Collection("ReviewForge service host")]
public sealed class TechnicianEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Disabled_verifier_disables_capability_and_rejects_draft_without_enqueue()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers().WithFindingVerification(false);
        using var client = factory.CreateClient();
        var capability = await client.GetFromJsonAsync<DraftCapabilityResponse>("/reviews/drafts/capability", Ct);
        Assert.False(capability!.Available);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await HttpClientJsonExtensions.PostAsJsonAsync(client, "/reviews/drafts", new SubmitReviewRequest("o", "p", "r", 1), Ct)).StatusCode);
        Assert.Equal(0, factory.Services.GetRequiredService<IReviewQueue>().ApproximateDepth);
    }

    [Fact]
    public async Task Draft_submission_status_details_and_decisions_use_safe_wire_contracts()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers().WithFindingVerification(true);
        using var client = factory.CreateClient();
        var response = await HttpClientJsonExtensions.PostAsJsonAsync(client, "/reviews/drafts", new SubmitReviewRequest("o", "p", "r", 1), Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var submitted = (await response.Content.ReadFromJsonAsync<SubmitReviewResponse>(Ct))!;
        var queued = (await client.GetFromJsonAsync<RunDetailsResponse>($"/reviews/{submitted.RunId}/details", Ct))!;
        Assert.Equal(RunState.Queued, queued.Status.State);
        Assert.False(queued.ObservationsAvailable);
        Assert.Null(queued.Counts);
        var store = factory.Services.GetRequiredService<IFindingStore>();
        var finding = new RichFinding { RuleId = "r", Title = "title", Severity = "high", Category = "bug", Description = "claim", DedupeKey = "key" };
        var observation = FindingObservation.Create(finding) with { Validation = FindingValidationStatus.Rejected, ValidationReason = "invalid-anchor" };
        var id = Guid.NewGuid();
        await store.SaveRunAsync(new(id, new("o", "p", "r", 2), "head", ReviewKind.Full, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, [], Pipeline: "ReviewDraft", FindingObservations: [observation]), Ct);
        var details = (await client.GetFromJsonAsync<RunDetailsResponse>($"/reviews/{id}/details", Ct))!;
        var candidate = Assert.Single(details.Findings);
        Assert.Equal(observation.Id.ToString("N"), candidate.FindingId);
        Assert.NotNull(candidate.Candidate);
        Assert.Null(candidate.AcceptedFinding);
        Assert.Equal(1, details.Counts!.Rejected);
        var request = new SetFindingDecisionRequest(candidate.FindingId, "head", FindingDecisionState.Rejected, "not applicable", 0);
        var saved = await HttpClientJsonExtensions.PostAsJsonAsync(client, $"/reviews/{id}/decisions", request, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var decision = (await saved.Content.ReadFromJsonAsync<FindingDecisionResponse>(Ct))!;
        Assert.Equal("header-key", decision.Decision!.Actor);
        Assert.Equal(1, decision.CurrentVersion);
        var conflict = await HttpClientJsonExtensions.PostAsJsonAsync(client, $"/reviews/{id}/decisions", request, Ct);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(1, (await conflict.Content.ReadFromJsonAsync<FindingDecisionResponse>(Ct))!.CurrentVersion);
        var invalid = await HttpClientJsonExtensions.PostAsJsonAsync(client, $"/reviews/{id}/decisions", request with { Reason = null, ExpectedVersion = 1 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(FindingDecisionStatus.InvalidDecision, (await invalid.Content.ReadFromJsonAsync<FindingDecisionResponse>(Ct))!.Status);
        var reread = (await client.GetFromJsonAsync<RunDetailsResponse>($"/reviews/{id}/details", Ct))!;
        Assert.Equal("not applicable", Assert.Single(reread.Findings).Decision!.Reason);
        Assert.Equal(HttpStatusCode.BadRequest, (await HttpClientJsonExtensions.PostAsJsonAsync(client, $"/reviews/{id}/decisions", request with { HeadSha = "wrong" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await HttpClientJsonExtensions.PostAsJsonAsync(client, $"/reviews/{id}/decisions", request with { FindingId = "other" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/reviews/{Guid.NewGuid()}/details", Ct)).StatusCode);
    }

    [Fact]
    public async Task History_is_bounded_member_checked_and_exposes_only_safe_actor_semantics()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<IFindingStore>();
        var id = Guid.NewGuid();
        var f = new RichFinding { RuleId = "r", Title = "title", Severity = "high", Category = "bug", Description = "claim", DedupeKey = "key" };
        var o = FindingObservation.Create(f);
        await store.SaveRunAsync(new(id, new("o", "p", "r", 2), "head", ReviewKind.Full, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, [], Pipeline: "ReviewDraft", FindingObservations: [o]), Ct);
        var key = o.Id.ToString("N");
        await HttpClientJsonExtensions.PostAsJsonAsync(client, $"/reviews/{id}/decisions", new SetFindingDecisionRequest(key, "head", FindingDecisionState.Rejected, "reason", 0), Ct);
        var history = (await client.GetFromJsonAsync<FindingDecisionHistoryResponse>($"/reviews/{id}/decisions/history?findingId={key}", Ct))!;
        Assert.Equal("reason", Assert.Single(history.Events).Reason);
        Assert.Equal("header-key", history.Events[0].Actor);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/reviews/{id}/decisions/history?findingId=other", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/reviews/{Guid.NewGuid()}/decisions/history?findingId={key}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/reviews/{id}/decisions/history?findingId=", Ct)).StatusCode);
    }

    [Fact]
    public async Task Publication_uses_submission_rate_limit()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers().WithSubmitLimit(1, 600);
        using var client = factory.CreateClient();
        var path = $"/reviews/{Guid.NewGuid()}/publish";
        var request = new PublishSelectedDraftRequest("head", []);
        Assert.Equal(HttpStatusCode.BadRequest, (await HttpClientJsonExtensions.PostAsJsonAsync(client, path, request, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await HttpClientJsonExtensions.PostAsJsonAsync(client, path, request, Ct)).StatusCode);
    }

    [Fact]
    public async Task Explicit_publication_rejects_missing_selection_and_returns_safe_conflicts()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        var pr = new PrKey("o", "p", "r", 4);
        var store = factory.Services.GetRequiredService<IFindingStore>();
        var f = new RichFinding { RuleId = "r", Title = "title", Severity = "high", Category = "bug", Description = "claim", DedupeKey = "key" };
        await store.SaveRunAsync(new(id, pr, "head-sha", ReviewKind.Full, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true,
            [new("key", "r", "high", "title", null, null, null, FindingJson: JsonSerializer.Serialize(f, new JsonSerializerOptions(JsonSerializerDefaults.Web)))], Pipeline: "ReviewDraft"), Ct);
        var path = $"/reviews/{id}/publish";
        foreach (var request in new[] { new PublishSelectedDraftRequest("head-sha", null), new PublishSelectedDraftRequest("head-sha", []) })
        {
            var response = await HttpClientJsonExtensions.PostAsJsonAsync(client, path, request, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("selection-required", (await response.Content.ReadFromJsonAsync<PublishSelectedDraftResult>(Ct))!.ErrorCode);
        }
        var selected = new PublishSelectedDraftRequest("head-sha", [new("key", 1)]);
        var unapproved = await HttpClientJsonExtensions.PostAsJsonAsync(client, path, selected, Ct);
        Assert.Equal(HttpStatusCode.Conflict, unapproved.StatusCode);
        Assert.Equal("approval-conflict", (await unapproved.Content.ReadFromJsonAsync<PublishSelectedDraftResult>(Ct))!.ErrorCode);
        await HttpClientJsonExtensions.PostAsJsonAsync(client, $"/reviews/{id}/decisions", new SetFindingDecisionRequest("key", "head-sha", FindingDecisionState.Approved, null, 0), Ct);
        var claims = factory.Services.GetRequiredService<InFlightClaims>();
        var claim = Guid.NewGuid();
        Assert.True(claims.TryClaim(pr, claim, out _));
        var busy = await HttpClientJsonExtensions.PostAsJsonAsync(client, path, selected, Ct);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.DoesNotContain(claim.ToString(), await busy.Content.ReadAsStringAsync(Ct));
        claims.Release(pr, claim);
        var posted = await HttpClientJsonExtensions.PostAsJsonAsync(client, path, selected, Ct);
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.Equal(1, (await posted.Content.ReadFromJsonAsync<PublishSelectedDraftResult>(Ct))!.PostedCount);
    }

    [Fact]
    public async Task Legacy_payload_is_unavailable_and_failed_status_does_not_leak_detail()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        var pr = new PrKey("o", "p", "r", 1);
        await factory.Services.GetRequiredService<IFindingStore>().SaveRunAsync(new(id, pr, "head", ReviewKind.Full,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, [new("legacy", "r", "high", "title", null, null, null)]), Ct);
        var tracker = factory.Services.GetRequiredService<RunTracker>();
        tracker.Register(new(id, pr, DateTimeOffset.UtcNow));
        tracker.Set(id, pr, RunState.Failed, "SECRET_EXCEPTION /private/path");
        foreach (var suffix in new[] { "", "/details" })
        {
            var text = await client.GetStringAsync($"/reviews/{id}{suffix}", Ct);
            Assert.DoesNotContain("SECRET_EXCEPTION", text);
            Assert.DoesNotContain("/private/path", text);
        }
        var details = (await client.GetFromJsonAsync<RunDetailsResponse>($"/reviews/{id}/details", Ct))!;
        Assert.False(details.ObservationsAvailable);
        Assert.Null(details.Counts);
        var legacy = Assert.Single(details.Findings);
        Assert.Null(legacy.AcceptedFinding);
        Assert.Null(legacy.Published);
        Assert.False(legacy.CanDecide);
    }
}
