using System.Text.Json;
using Bunit;
using Microsoft.JSInterop;
using Microsoft.Extensions.DependencyInjection;
using ReviewForge.Service.Components.Pages;
using Xunit;
using TestContext = Xunit.TestContext;

namespace ReviewForge.Service.Tests;

public class DashboardBrowserClientTests
{
    [Theory]
    [InlineData(200, BrowserOutcome.Ok)]
    [InlineData(202, BrowserOutcome.Accepted)]
    [InlineData(400, BrowserOutcome.Invalid)]
    [InlineData(401, BrowserOutcome.Unauthenticated)]
    [InlineData(403, BrowserOutcome.Forbidden)]
    [InlineData(409, BrowserOutcome.Conflict)]
    [InlineData(429, BrowserOutcome.RateLimited)]
    [InlineData(503, BrowserOutcome.Unavailable)]
    [InlineData(500, BrowserOutcome.NetworkError)]
    public async Task Adapter_maps_safe_outcomes_without_direct_service_access(int status, BrowserOutcome outcome)
    {
        await using var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./dashboard-http.js");
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true)
            .SetResult(new(status, status is 200 or 202 ? JsonSerializer.SerializeToElement(new SubmitReviewResponse(Guid.NewGuid(), "/reviews/1")) : null));
        await using var client = new DashboardBrowserClient(context.JSInterop.JSRuntime);
        var result = await client.SubmitReviewAsync(new("o", "p", "r", 1, true), TestContext.Current.CancellationToken);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(status is 200 or 202, result.Value is not null);
        var invocation = module.Invocations["send"].Single();
        Assert.Equal("POST", invocation.Arguments[1]);
        Assert.Equal("/reviews", invocation.Arguments[2]);
        Assert.True(Assert.IsType<SubmitReviewRequest>(invocation.Arguments[3]).ForceReview);
    }

    [Fact]
    public async Task Adapter_get_cancellation_disconnect_and_bad_json_are_safe()
    {
        await using var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./dashboard-http.js");
        var send = module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true);
        var abort = module.SetupVoid("abort", _ => true);
        abort.SetVoidResult();
        await using var client = new DashboardBrowserClient(context.JSInterop.JSRuntime);
        send.SetResult(new(200, JsonSerializer.SerializeToElement("not a dashboard")));
        Assert.Equal(BrowserOutcome.NetworkError, (await client.GetDashboardAsync(TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal("GET", module.Invocations["send"].Single().Arguments[1]);
        Assert.Equal("/reviews/dashboard", module.Invocations["send"].Single().Arguments[2]);
        send.SetException(new JSException("disconnected"));
        Assert.Equal(BrowserOutcome.NetworkError, (await client.GetDashboardAsync(TestContext.Current.CancellationToken)).Outcome);
        send.SetException(new OperationCanceledException());
        Assert.Equal(BrowserOutcome.Cancelled, (await client.GetDashboardAsync(TestContext.Current.CancellationToken)).Outcome);
        Assert.Single(module.Invocations["abort"]);
        abort.SetException(new JSDisconnectedException("closed"));
        Assert.Equal(BrowserOutcome.Cancelled, (await client.GetDashboardAsync(TestContext.Current.CancellationToken)).Outcome);
        send.SetException(new JSDisconnectedException("closed"));
        Assert.Equal(BrowserOutcome.NetworkError, (await client.GetDashboardAsync(TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task Technician_methods_use_typed_ids_and_safe_deserializable_decisions()
    {
        await using var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./dashboard-http.js");
        var send = module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true);
        send.SetResult(new(404, null));
        await using var client = new DashboardBrowserClient(context.JSInterop.JSRuntime);
        var id = Guid.NewGuid();
        Assert.Equal(BrowserOutcome.NotFound, (await client.GetRunStatusAsync(id, TestContext.Current.CancellationToken)).Outcome);
        await client.GetRunDetailsAsync(id, TestContext.Current.CancellationToken);
        await client.GetDraftCapabilityAsync(TestContext.Current.CancellationToken);
        await client.SubmitDraftAsync(new("o", "p", "r", 1), TestContext.Current.CancellationToken);
        var decision = new FindingDecisionDto(id, "id/../other", "head", ReviewForge.Core.Domain.FindingDecisionState.Approved, null, "dashboard-session", DateTimeOffset.UtcNow, 1);
        send.SetResult(new(409, JsonSerializer.SerializeToElement(new FindingDecisionResponse(FindingDecisionStatus.Conflict, 1, decision))));
        var result = await client.SetFindingDecisionAsync(id, new(decision.FindingId, "head", decision.State, null, 0), TestContext.Current.CancellationToken);
        Assert.Equal(BrowserOutcome.Conflict, result.Outcome);
        Assert.Equal(decision, result.Value!.Decision);
        Assert.Equal(new[] { $"/reviews/{id:D}", $"/reviews/{id:D}/details", "/reviews/drafts/capability", "/reviews/drafts", $"/reviews/{id:D}/decisions" },
            module.Invocations["send"].Select(i => (string)i.Arguments[2]!));
    }

    [Fact]
    public async Task Batch_method_keeps_versions_and_reasons_in_body_and_reads_partial_failure()
    {
        await using var context = new BunitContext(); var module = context.JSInterop.SetupModule("./dashboard-http.js");
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true)
            .SetResult(new(409, JsonSerializer.SerializeToElement(new BatchFindingDecisionResponse("busy", [new("id", BatchFindingDecisionStatus.NotAttempted)]))));
        await using var client = new DashboardBrowserClient(context.JSInterop.JSRuntime); var id = Guid.NewGuid();
        var request = new BatchFindingDecisionRequest("head", [new("id", 9, ReviewForge.Core.Domain.FindingDecisionState.Rejected, "reason")]);
        var result = await client.SetBatchFindingDecisionsAsync(id, request, TestContext.Current.CancellationToken);
        Assert.Equal(BrowserOutcome.Conflict, result.Outcome); Assert.Equal("busy", result.Value!.ErrorCode);
        var call = Assert.Single(module.Invocations["send"]); Assert.Equal("POST", call.Arguments[1]);
        Assert.Equal($"/reviews/{id:D}/decisions/batch", call.Arguments[2]); Assert.Same(request, call.Arguments[3]);
    }

    [Fact]
    public async Task Publication_method_keeps_selection_in_body_and_reads_safe_partial_outcomes()
    {
        await using var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./dashboard-http.js");
        var response = new PublishSelectedDraftResult(false, "publication-unknown", [new("id/../other", PublishFindingStatus.Unknown)]);
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true)
            .SetResult(new(409, JsonSerializer.SerializeToElement(response)));
        await using var client = new DashboardBrowserClient(context.JSInterop.JSRuntime);
        var id = Guid.NewGuid();
        var request = new PublishSelectedDraftRequest("head", [new("id/../other", 3)]);
        var result = await client.PublishSelectedDraftAsync(id, request, TestContext.Current.CancellationToken);
        Assert.Equal(BrowserOutcome.Conflict, result.Outcome);
        Assert.Equal("publication-unknown", result.Value!.ErrorCode);
        Assert.Equal(PublishFindingStatus.Unknown, Assert.Single(result.Value.Findings).Status);
        var invocation = Assert.Single(module.Invocations["send"]);
        Assert.Equal("POST", invocation.Arguments[1]);
        Assert.Equal($"/reviews/{id:D}/publish", invocation.Arguments[2]);
        Assert.Same(request, invocation.Arguments[3]);
    }

    [Fact]
    public async Task History_query_encodes_scope_without_route_or_query_injection()
    {
        await using var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./dashboard-http.js");
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true).SetResult(new(503, null));
        await using var client = new DashboardBrowserClient(context.JSInterop.JSRuntime);
        await client.GetRunWorklistAsync(50, 50, new("o&count=100", "p/#?", "r=other", 2), false, TestContext.Current.CancellationToken);
        var call = Assert.Single(module.Invocations["send"]);
        Assert.Equal("GET", call.Arguments[1]);
        Assert.Equal("/reviews/history?offset=50&count=50&attentionOnly=false&org=o%26count%3D100&project=p%2F%23%3F&repositoryId=r%3Dother&prId=2", call.Arguments[2]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetRunWorklistAsync(-1, ct: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetRunWorklistAsync(count: 101, ct: TestContext.Current.CancellationToken));
        Assert.Single(module.Invocations["send"]);
    }

    [Fact]
    public async Task Decision_history_is_a_single_lazy_member_query_with_encoded_id()
    {
        await using var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./dashboard-http.js");
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true)
            .SetResult(new(200, System.Text.Json.JsonSerializer.SerializeToElement(new FindingDecisionHistoryResponse([]))));
        await using var client = new DashboardBrowserClient(context.JSInterop.JSRuntime);
        var id = Guid.NewGuid();
        var result = await client.GetFindingDecisionHistoryAsync(id, "key/with&count=999", TestContext.Current.CancellationToken);
        Assert.Equal(BrowserOutcome.Ok, result.Outcome);
        Assert.Empty(result.Value!.Events);
        var call = Assert.Single(module.Invocations["send"]);
        Assert.Equal($"/reviews/{id:D}/decisions/history?findingId=key%2Fwith%26count%3D999", call.Arguments[2]);
        Assert.Equal("GET", call.Arguments[1]);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetFindingDecisionHistoryAsync(id, "", TestContext.Current.CancellationToken));
        Assert.Single(module.Invocations["send"]);
    }

    [Fact]
    public async Task Shell_renders_native_controls_and_logout_without_data_or_submission_services()
    {
        await using var context = new BunitContext();
        context.Services.AddScoped<DashboardConfirmation>();
        context.Services.AddScoped<DashboardWorklistState>();
        context.Services.AddScoped<DashboardBrowserClient>();
        context.Services.AddSingleton<TimeProvider>(new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./dashboard-http.js")
            .Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true).SetResult(new(503, null));
        var shell = context.Render<Dashboard>();
        Assert.Contains("Runs needing attention", shell.Markup);
        Assert.Equal("/dashboard/start", shell.Find("a.dashboard-start").GetAttribute("href"));
        Assert.Equal("Refresh", shell.Find("button[aria-label='Refresh dashboard']").TextContent.Trim());
        Assert.Equal("submit", shell.Find("form button").GetAttribute("type"));
        Assert.DoesNotContain("rz-", shell.Markup);
        Assert.Equal("/dashboard/logout", shell.Find("form").GetAttribute("action"));
        Assert.Equal("post", shell.Find("form").GetAttribute("method"));
        Assert.DoesNotContain("apiKey", shell.Markup);
    }
}
