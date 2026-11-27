using Bunit;
using Microsoft.JSInterop;
using Microsoft.Extensions.DependencyInjection;
using ReviewForge.Service.Components.Dashboard;
using Xunit;

namespace ReviewForge.Service.Tests;

public class WorkbenchShellTests
{
    [Fact]
    public async Task Confirmation_is_single_pending_and_cancellation_releases_it_without_replaying()
    {
        var confirmation = new DashboardConfirmation();
        var changes = 0;
        confirmation.Changed += () => changes++;
        using var lifetime = new CancellationTokenSource();
        var first = confirmation.ConfirmAsync("Original prompt", lifetime.Token);
        Assert.False(await confirmation.ConfirmAsync("Duplicate", Xunit.TestContext.Current.CancellationToken));
        Assert.Equal("Original prompt", confirmation.Prompt);
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Null(confirmation.Prompt);
        confirmation.Complete(true); // A stale completion cannot become a later command.
        var second = confirmation.ConfirmAsync("New prompt", Xunit.TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        confirmation.Complete(false);
        Assert.False(await second);
        confirmation.Notify("Safe feedback");
        Assert.Equal("Safe feedback", confirmation.Message);
        Assert.Equal(5, changes);
    }

    [Fact]
    public async Task Native_dialog_enters_focus_once_and_closes_on_disposal()
    {
        await using var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./dashboard-dialog.js");
        module.SetupVoid("open", _ => true).SetVoidResult();
        module.SetupVoid("close", _ => true).SetVoidResult();
        var dialog = context.Render<WorkbenchDialog>(p => p.Add(x => x.Label, "Confirm publication")
            .AddChildContent("<button data-dialog-cancel autofocus>Cancel</button>"));
        Assert.Equal("Confirm publication", dialog.Find("dialog").GetAttribute("aria-label"));
        Assert.Single(module.Invocations["open"]);
        dialog.Render(p => p.Add(x => x.Label, "Updated confirmation"));
        Assert.Single(module.Invocations["open"]);
        await context.DisposeComponentsAsync();
        Assert.Single(module.Invocations["close"]);
    }

    [Fact]
    public async Task Native_dialog_tolerates_disconnected_circuit_on_entry_and_disposal()
    {
        await using var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./dashboard-dialog.js");
        module.SetupVoid("open", _ => true).SetException(new JSDisconnectedException("disconnected"));
        module.SetupVoid("close", _ => true).SetException(new JSDisconnectedException("disconnected"));
        var dialog = context.Render<WorkbenchDialog>();
        Assert.NotNull(dialog.Find("dialog"));
        await context.DisposeComponentsAsync();
    }

    [Fact]
    public async Task Disposing_while_dialog_module_loads_never_opens_a_retired_dialog()
    {
        await using var context = new BunitContext();
        var module = new LateDialogModule();
        context.Services.AddSingleton<IJSRuntime>(module);
        context.Render<WorkbenchDialog>();
        await context.DisposeComponentsAsync();
        module.Loaded.SetResult(module);
        await module.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(0, module.OpenCalls);
    }

    // Single-use module double for the disposal-during-import interop boundary.
#pragma warning disable BL0016 // Single-use controlled test double; no real JS/circuit calls occur here.
    private sealed class LateDialogModule : IJSRuntime, IJSObjectReference
    {
        public int OpenCalls { get; private set; }
        public TaskCompletionSource<IJSObjectReference> Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeCoreAsync<TValue>(identifier);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeCoreAsync<TValue>(identifier);
        private async ValueTask<TValue> InvokeCoreAsync<TValue>(string identifier)
        {
            if (identifier == "import") return (TValue)(object)await Loaded.Task;
            if (identifier == "open") OpenCalls++;
            return default!;
        }
        public ValueTask DisposeAsync() { Disposed.TrySetResult(); return ValueTask.CompletedTask; }
    }
#pragma warning restore BL0016

    [Fact]
    public async Task Theme_control_exposes_system_light_and_dark_without_finding_or_session_data()
    {
        await using var context = new BunitContext();
        var picker = context.Render<ThemePicker>();
        Assert.Equal("Color theme", picker.Find("select").GetAttribute("aria-label"));
        Assert.Equal(new[] { "system", "light", "dark" }, picker.FindAll("option").Select(x => x.GetAttribute("value")));
        Assert.DoesNotContain("apiKey", picker.Markup);
    }
}
