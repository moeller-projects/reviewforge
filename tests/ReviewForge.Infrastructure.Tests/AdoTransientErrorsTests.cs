using System.Net;
using System.Reflection;
using Microsoft.VisualStudio.Services.WebApi;
using ReviewForge.Infrastructure.Ado;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public class AdoTransientErrorsTests
{
    // The SDK's (HttpStatusCode, string) ctor is not public; reflection is the only
    // way to build a response exception carrying a specific status code.
    private static VssServiceResponseException AdoError(HttpStatusCode code)
    {
        var ctor = typeof(VssServiceResponseException).GetConstructor(
                       BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                       binder: null,
                       [typeof(HttpStatusCode), typeof(string)],
                       modifiers: null)
                   ?? throw new InvalidOperationException("VssServiceResponseException(HttpStatusCode, string) not found");
        return (VssServiceResponseException)ctor.Invoke([code, "response body"]);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(404, false)]
    [InlineData(200, false)]
    public void Classifies_http_status_codes(int code, bool expected)
    {
        var ex = AdoError((HttpStatusCode)code);
        Assert.Equal(expected, AdoTransientErrors.IsTransient(ex));
    }

    [Fact]
    public void Caller_timeout_is_transient()
        => Assert.True(AdoTransientErrors.IsTransient(new TaskCanceledException()));

    [Fact]
    public void Connection_level_failures_are_transient()
        => Assert.True(AdoTransientErrors.IsTransient(new HttpRequestException("connection reset")));

    [Fact]
    public void Unrelated_exceptions_are_not_transient()
        => Assert.False(AdoTransientErrors.IsTransient(new InvalidOperationException("nope")));

    [Fact]
    public void Probes_retry_after_from_exception_data()
    {
        var ex = new InvalidOperationException("no message hint");
        ex.Data["Retry-After"] = "7";
        Assert.Equal(TimeSpan.FromSeconds(7), AdoTransientErrors.ProbeRetryAfter(ex));
    }

    [Fact]
    public void Probes_retry_after_from_message()
    {
        var ex = new InvalidOperationException("throttled, retry-after: 9");
        Assert.Equal(TimeSpan.FromSeconds(9), AdoTransientErrors.ProbeRetryAfter(ex));
    }

    [Fact]
    public void Probe_walks_inner_exception_chain()
    {
        var inner = new InvalidOperationException("plain");
        inner.Data["Retry-After"] = "4";
        var outer = new InvalidOperationException("outer", inner);
        Assert.Equal(TimeSpan.FromSeconds(4), AdoTransientErrors.ProbeRetryAfter(outer));
    }

    [Fact]
    public void Probe_ignores_unparseable_hints()
    {
        var ex = new InvalidOperationException("retry-after: nope");
        ex.Data["Retry-After"] = "abc";
        Assert.Null(AdoTransientErrors.ProbeRetryAfter(ex));
    }

    [Fact]
    public void Probe_ignores_negative_data_hint()
    {
        var ex = new InvalidOperationException("no message hint");
        ex.Data["Retry-After"] = "-1";

        Assert.Null(AdoTransientErrors.ProbeRetryAfter(ex));
    }

    [Fact]
    public void Probe_returns_null_without_hint()
        => Assert.Null(AdoTransientErrors.ProbeRetryAfter(new InvalidOperationException("plain")));

    [Fact]
    public void Http_status_code_surfaces_for_classification()
    {
        var ex = AdoError(HttpStatusCode.TooManyRequests);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.HttpStatusCode);
        Assert.True(AdoTransientErrors.IsTransient(ex));
    }
}