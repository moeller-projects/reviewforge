using Bunit;
using Microsoft.Extensions.Time.Testing;
using ReviewForge.Service.Components.Dashboard;
using Xunit;

namespace ReviewForge.Service.Tests;

public class DashboardRefreshTests
{
    [Fact]
    public async Task Adaptive_timer_serializes_and_coalesces_manual_and_accepted_requests()
    {
        await using var context = new BunitContext();
        // The production loop is dispatcher-owned. Keep fake-clock advances on that same
        // dispatcher so Yield drains queued re-arming work before advancing the next interval.
        await context.Renderer.Dispatcher.InvokeAsync(async () =>
        {
            var clock = new FakeTimeProvider();
            var active = false;
            var calls = 0;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var refresh = new DashboardRefreshLoop(clock, () => active, async ct =>
            {
                calls++;
                if (calls == 2) { entered.SetResult(); await release.Task.WaitAsync(ct); }
            });
            refresh.Start();
            Assert.Equal(1, calls);
            clock.Advance(TimeSpan.FromSeconds(24));
            Assert.Equal(1, calls);
            clock.Advance(TimeSpan.FromSeconds(1));
            await entered.Task;
            var manual = refresh.RequestAsync();
            var accepted = refresh.RequestAsync();
            Assert.Equal(2, calls);
            active = true;
            release.SetResult();
            await Task.WhenAll(manual, accepted);
            Assert.Equal(3, calls);
            await Task.Yield();
            clock.Advance(TimeSpan.FromSeconds(3));
            await WaitForAsync(() => calls == 4);
            refresh.Stop();
            clock.Advance(TimeSpan.FromMinutes(1));
            await refresh.RequestAsync();
            Assert.Equal(4, calls);
        });
    }

    [Fact]
    public async Task Accepted_manual_refresh_interrupts_idle_wait_and_switches_to_active_cadence()
    {
        await using var context = new BunitContext();
        await context.Renderer.Dispatcher.InvokeAsync(async () =>
        {
            var clock = new FakeTimeProvider();
            var active = false;
            var calls = 0;
            await using var refresh = new DashboardRefreshLoop(clock, () => active, _ => { calls++; return Task.CompletedTask; });
            refresh.Start();
            clock.Advance(TimeSpan.FromSeconds(1));
            active = true;
            await refresh.RequestAsync();
            await Task.Yield();
            clock.Advance(TimeSpan.FromSeconds(3));
            await WaitForAsync(() => calls == 3);
            active = false;
            await refresh.RequestAsync();
            await Task.Yield();
            var before = calls;
            clock.Advance(TimeSpan.FromSeconds(24));
            Assert.Equal(before, calls);
            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitForAsync(() => calls == before + 1);
        });
    }

    [Fact]
    public async Task Disposal_cancels_and_awaits_inflight_and_prevents_later_requests()
    {
        var calls = 0;
        var ended = false;
        var refresh = new DashboardRefreshLoop(new FakeTimeProvider(), () => true, async ct =>
        {
            calls++;
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { ended = true; }
        });
        refresh.Start();
        await refresh.DisposeAsync();
        await refresh.RequestAsync();
        Assert.True(ended);
        Assert.Equal(1, calls);
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }
}
