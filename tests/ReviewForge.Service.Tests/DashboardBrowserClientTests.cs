using System.Text.Json;
using Bunit;
using Microsoft.JSInterop;
using Microsoft.Extensions.DependencyInjection;
using ReviewForge.Service.Components.Pages;
using Radzen;
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
    public async Task Shell_renders_radzen_and_logout_without_data_or_submission_services()
    {
        await using var context = new BunitContext();
        context.Services.AddRadzenComponents();
        context.Services.AddScoped<DashboardBrowserClient>();
        context.Services.AddSingleton<TimeProvider>(new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./dashboard-http.js")
            .Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true).SetResult(new(503, null));
        var shell = context.Render<Dashboard>();
        Assert.Contains("ReviewForge dashboard", shell.Markup);
        Assert.Contains("rz-button", shell.Markup);
        Assert.Equal("/dashboard/logout", shell.Find("form").GetAttribute("action"));
        Assert.Equal("post", shell.Find("form").GetAttribute("method"));
        Assert.DoesNotContain("apiKey", shell.Markup);
    }
}
