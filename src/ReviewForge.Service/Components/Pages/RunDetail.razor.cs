using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Components.Dashboard;

namespace ReviewForge.Service.Components.Pages;

public partial class RunDetail
{
    [Parameter] public Guid RunId { get; set; }
    private RunDetailsResponse? _Details;
    private DashboardRefreshLoop? _Refresh;
    private CancellationTokenSource? _Route;
    private readonly List<Task> _Retired = [];
    private FindingWorkspaceState _Workspace = new();
    private HashSet<string> _Selected => _Workspace.Selected;
    private readonly HashSet<string> _Unknown = new(StringComparer.Ordinal);
    private PublishSelectedDraftRequest? _Confirmation;
    private IReadOnlyList<PublicationPreviewItem> _PublicationPreview = [];
    private bool HasUnsaved => _Workspace.Rejection?.HasUnsaved == true || _Workspace.Drafts.Values.Any(d => d.Dirty);
    private PublishSelectedDraftResult? _Publication;
    private BatchFindingDecisionResponse? _Batch;
    private FindingDecisionHistoryResponse? _History;
    private string? _HistoryId, _Message;
    private string _Section = "Findings";
    private Guid? _LoadedId;
    private long _Generation;
    private bool _PendingRoute, _Disposed, _Loading, _Saving, _Posting, _HistoryLoading, _Reconciling, _Stale, _AuthenticationExpired, _RefreshRequested;
    private bool Busy => _Loading || _Saving || _Posting || _HistoryLoading || _Reconciling || _Confirmation is not null;
    private bool HeadChanged => !string.IsNullOrWhiteSpace(_Details?.ReviewedHeadSha)
        && !string.IsNullOrWhiteSpace(_Details?.Source?.CurrentHeadSha)
        && !string.Equals(_Details.ReviewedHeadSha, _Details.Source.CurrentHeadSha, StringComparison.OrdinalIgnoreCase);
    private int EligibleSelected => _Details?.Findings.Count(f => _Selected.Contains(f.FindingId) && CanSelect(f)) ?? 0;
    private bool CanSelect(RunFindingDto f) => PublicationExclusion(f) is null;
    private string? PublicationExclusion(RunFindingDto f) => HeadChanged ? "Reviewed head changed"
        : string.IsNullOrWhiteSpace(_Details?.ReviewedHeadSha) ? "Reviewed head unavailable"
        : _Details.Status.Kind != RunKind.ReviewDraft || _Details.Status.State != RunState.Completed ? "Read-only run"
        : PublicationPreviewItem.Exclusion(f, _Unknown.Contains(f.FindingId));

    protected override void OnParametersSet()
    {
        if (_LoadedId == RunId) return;
        _LoadedId = RunId;
        _Generation++;
        _Route?.Cancel();
        if (_Refresh is not null) _Retired.Add(_Refresh.DisposeAsync().AsTask());
        _Route?.Dispose();
        _Route = new();
        _Refresh = null;
        _Details = null;
        _Selected.Clear();
        _Unknown.Clear();
        _Confirmation = null;
        _PublicationPreview = [];
        _Publication = null;
        _Batch = null;
        _History = null;
        _HistoryId = _Message = null;
        _Section = "Findings";
        _Workspace = new();
        _Loading = true;
        _Saving = _Posting = _HistoryLoading = _Reconciling = _Stale = _AuthenticationExpired = false;
        _PendingRoute = true;
    }
    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_PendingRoute || _Disposed) return Task.CompletedTask;
        _PendingRoute = false;
        var generation = _Generation;
        var id = RunId;
        _RefreshRequested = true;
        _Refresh = new(Clock, () => _Details?.Status.State is RunState.Queued or RunState.Running,
            ct => InvokeAsync(() => LoadAsync(id, generation, ct)));
        _Refresh.Start();
        return Task.CompletedTask;
    }
    private bool Current(long generation) => !_Disposed && generation == _Generation;
    private async Task LoadAsync(Guid id, long generation, CancellationToken ct)
    {
        if (!Current(generation) || _AuthenticationExpired || _Saving || _Posting || _HistoryLoading || _Reconciling || _Confirmation is not null) return;
        if (!_RefreshRequested && _Details?.Status.State is not (RunState.Queued or RunState.Running)) return;
        _RefreshRequested = false;
        _Loading = true;
        StateHasChanged();
        var result = await Browser.GetRunDetailsAsync(id, ct);
        if (!Current(generation)) return;
        _Loading = false;
        if (result.Outcome == BrowserOutcome.Ok && result.Value is { } value)
        {
            _Details = value;
            _Stale = false;
            _Workspace.Reconcile(value.Findings);
            foreach (var finding in value.Findings.Where(f => f.PublicationUnknown)) _Unknown.Add(finding.FindingId);
            _Unknown.RemoveWhere(key => value.Findings.Any(f => f.FindingId == key && f.Published == true));
        }
        else if (result.Outcome != BrowserOutcome.Cancelled)
        {
            _Stale = true;
            if (result.Outcome == BrowserOutcome.NotFound) _Details = null;
            Fail(result.Outcome, result.Outcome == BrowserOutcome.NotFound ? "Run not found." : "Could not refresh run. Any previous snapshot is stale; actions are disabled until refresh succeeds.");
        }
        StateHasChanged();
    }
    private Task RefreshAsync()
    {
        if (_Disposed || _AuthenticationExpired) return Task.CompletedTask;
        _RefreshRequested = true;
        return _Refresh?.RequestAsync() ?? Task.CompletedTask;
    }
    private void PreparePublication()
    {
        if (Busy || _Stale || _AuthenticationExpired || HeadChanged || _Details is null) return;
        var selection = _Details.Findings.Where(f => _Selected.Contains(f.FindingId) && CanSelect(f))
            .Select(f => new PublishFindingSelection(f.FindingId, f.DecisionVersion)).ToArray();
        if (selection.Length == 0 || string.IsNullOrWhiteSpace(_Details.ReviewedHeadSha)) return;
        _PublicationPreview = _Details.Findings.Where(f => _Selected.Contains(f.FindingId))
            .Select(f => PublicationPreviewItem.From(f, PublicationExclusion(f))).ToArray();
        _Confirmation = new(_Details.ReviewedHeadSha, Array.AsReadOnly(selection));
    }
    private void CancelPublication() { if (!_Posting) _Confirmation = null; }
    private async Task SaveAsync(SetFindingDecisionRequest request)
    {
        if (Busy || _Stale || _AuthenticationExpired || _Route is null || _Details is null
            || _Details.Status.Kind != RunKind.ReviewDraft || _Details.Status.State != RunState.Completed) return;
        var finding = _Details.Findings.FirstOrDefault(f => f.FindingId == request.FindingId);
        if (finding is null || _Workspace.DecisionBlockReason(finding, _Details.ReviewedHeadSha ?? "", false) is not null
            || request.HeadSha != _Details.ReviewedHeadSha || request.ExpectedVersion != finding.DecisionVersion
            || (request.State == FindingDecisionState.Rejected && string.IsNullOrWhiteSpace(request.Reason))) return;
        var order = _Workspace.Matching(_Details.Findings).Select(f => f.FindingId).ToArray();
        var generation = _Generation;
        var id = RunId;
        _Saving = true;
        var result = await Browser.SetFindingDecisionAsync(id, request, _Route.Token);
        if (!Current(generation)) return;
        _Saving = false;
        var confirmed = result.Outcome == BrowserOutcome.Ok && result.Value?.Status == FindingDecisionStatus.Saved
            && result.Value.CurrentVersion > request.ExpectedVersion;
        if (confirmed) _Workspace.Drafts.Remove(request.FindingId);
        _History = null;
        _HistoryId = null;
        if (result.Outcome is BrowserOutcome.Ok or BrowserOutcome.Conflict)
        {
            await RefreshAsync();
            if (!Current(generation)) return;
            if (confirmed && !_Stale && _Details is not null) _Workspace.AdvanceAfterSave(request.FindingId, order, _Details.Findings);
            _Message = result.Outcome == BrowserOutcome.Conflict
                ? "The decision changed or the PR is busy. Refreshed authoritative data; your edit was not overwritten or resubmitted. Review before saving again."
                : confirmed ? _Stale ? "Decision saved, but refresh failed. Refresh before continuing; review did not advance. Approval does not publish findings."
                    : "Decision saved. Approval does not publish findings." : "Decision was not confirmed. Review authoritative data before editing again.";
        }
        else Fail(result.Outcome, "Decision was not confirmed. Refresh before editing again.");
    }
    private async Task BeforeRejectionNavigation(LocationChangingContext context)
    {
        if (_Saving) { context.PreventNavigation(); return; }
        if (!HasUnsaved) return;
        try
        {
            if (!await JS.InvokeAsync<bool>("confirm", "Discard unsaved decision and rejection reasons and leave this run? Saved decisions will not be undone.")) context.PreventNavigation();
        }
        catch (JSDisconnectedException) { context.PreventNavigation(); }
    }
    private async Task SaveBatchAsync(BatchFindingDecisionRequest request)
    {
        if (Busy || _Stale || _AuthenticationExpired || _Route is null || _Details is null
            || _Details.Status.Kind != RunKind.ReviewDraft || _Details.Status.State != RunState.Completed
            || request.HeadSha != _Details.ReviewedHeadSha || request.Selections.Count is < 1 or > FindingDecisionService.MaxBatchSize) return;
        // Validate the frozen request, not the mutable selection or current filters.
        if (request.Selections.Select(s => s.FindingId).Distinct(StringComparer.Ordinal).Count() != request.Selections.Count
            || request.Selections.Any(s => !_Details.Findings.Any(f => f.FindingId == s.FindingId && f.DecisionVersion == s.ExpectedVersion
                && _Workspace.DecisionBlockReason(f, request.HeadSha, false) is null) || _Workspace.Drafts.GetValueOrDefault(s.FindingId)?.Dirty == true)) return;
        var generation = _Generation;
        _Saving = true;
        var result = await Browser.SetBatchFindingDecisionsAsync(RunId, request, _Route.Token);
        if (!Current(generation)) return;
        _Saving = false;
        _History = null; _HistoryId = null;
        var structured = result.Outcome is BrowserOutcome.Ok or BrowserOutcome.Conflict or BrowserOutcome.Invalid or BrowserOutcome.NotFound
            && result.Value is { } value && value.Items.Count == request.Selections.Count
            && request.Selections.All(s => value.Items.Count(i => i.FindingId == s.FindingId) == 1);
        _Batch = structured ? result.Value : new("unconfirmed", request.Selections.Select(s => new BatchFindingDecisionItem(s.FindingId, BatchFindingDecisionStatus.Unknown)).ToArray());
        foreach (var item in _Batch!.Items)
        {
            var submitted = request.Selections.First(s => s.FindingId == item.FindingId);
            if (item.Status == BatchFindingDecisionStatus.Saved && item.CurrentVersion == submitted.ExpectedVersion + 1)
            { _Selected.Remove(item.FindingId); _Workspace.Drafts.Remove(item.FindingId); }
            else
            {
                // A malformed success is not confirmation of a write.
                if (item.Status == BatchFindingDecisionStatus.Saved)
                    _Batch = _Batch with { Items = _Batch.Items.Select(i => i.FindingId == item.FindingId ? i with { Status = BatchFindingDecisionStatus.Unknown } : i).ToArray() };
                _Selected.Add(item.FindingId);
            }
        }
        if (request.Selections.All(s => s.State == FindingDecisionState.Rejected)) _Workspace.Rejection?.Apply(_Batch!);
        if (structured)
        {
            await RefreshAsync();
            if (!Current(generation)) return;
            _Message = _Batch.ErrorCode is null ? "Batch processed. Review per-finding outcomes; approval does not publish. Review did not advance."
                : "Batch stopped or rejected. Review per-finding outcomes and refresh before deciding again. Nothing is retried automatically.";
        }
        else
        {
            Fail(result.Outcome, "Batch decisions could not be confirmed. Refresh authoritative data before editing again. Nothing is retried automatically.");
            _Selected.UnionWith(request.Selections.Select(s => s.FindingId));
            _Stale = true;
        }
    }
    private async Task PostAsync()
    {
        if (_Posting || _Confirmation is not { Selections.Count: > 0 } request || _Route is null || _AuthenticationExpired || _Stale || HeadChanged) return;
        var generation = _Generation;
        var id = RunId;
        _Posting = true;
        var result = await Browser.PublishSelectedDraftAsync(id, request, _Route.Token);
        if (!Current(generation)) return;
        _Posting = false;
        _Confirmation = null;
        _Publication = result.Value;
        if (result.Value is { } publication)
            foreach (var item in publication.Findings.Where(f => f.Status == PublishFindingStatus.Unknown)) _Unknown.Add(item.FindingId);
        if (result.Outcome is BrowserOutcome.Ok or BrowserOutcome.Conflict or BrowserOutcome.Invalid)
        {
            await RefreshAsync();
            if (!Current(generation)) return;
            _Message = result.Outcome == BrowserOutcome.Conflict
                ? "Publication conflict: approval or reviewed head may have changed. Refreshed data; nothing is automatically retried."
                : "Review the per-finding publication outcomes. Nothing is automatically retried.";
        }
        else
        {
            foreach (var selection in request.Selections) _Unknown.Add(selection.FindingId);
            Fail(result.Outcome, "Publication could not be confirmed. An external comment may exist. Refresh for reconciliation; do not retry automatically.");
            _Stale = true;
        }
    }
    private async Task ReconcileAsync()
    {
        if (Busy || _AuthenticationExpired || _Route is null || _Unknown.Count == 0) return;
        var generation = _Generation;
        var id = RunId;
        _Reconciling = true;
        var result = await Browser.ReconcileDraftAsync(id, new(_Unknown.ToArray()), _Route.Token);
        if (!Current(generation)) return;
        _Reconciling = false;
        if (result.Outcome == BrowserOutcome.Ok && result.Value is { } reconciliation)
        {
            _Publication = reconciliation;
            foreach (var item in reconciliation.Findings.Where(f => f.Status == PublishFindingStatus.AlreadyPosted)) _Unknown.Remove(item.FindingId);
            await RefreshAsync();
            if (!Current(generation)) return;
            _Message = reconciliation.UnknownCount > 0
                ? "No matching external thread was observed for some findings. Their outcomes remain unknown. Check the PR, reconcile again, or start a new draft; nothing was reposted."
                : "Publication state reconciled from existing threads. No findings were posted by reconciliation.";
        }
        else Fail(result.Outcome, "Could not reconcile publication state. Outcomes remain unknown; no findings were reposted.");
    }

    private async Task HistoryAsync(string findingId)
    {
        if (Busy || _Stale || _AuthenticationExpired || _Route is null) return;
        var generation = _Generation;
        _HistoryLoading = true;
        _HistoryId = findingId;
        _History = null;
        var result = await Browser.GetFindingDecisionHistoryAsync(RunId, findingId, _Route.Token);
        if (!Current(generation)) return;
        _HistoryLoading = false;
        if (result.Outcome == BrowserOutcome.Ok) _History = result.Value;
        else Fail(result.Outcome, "Decision history is unavailable. No history has been inferred.");
    }
    private void Fail(BrowserOutcome outcome, string message)
    {
        if (outcome is BrowserOutcome.Unauthenticated or BrowserOutcome.Forbidden)
        {
            _AuthenticationExpired = true;
            _Confirmation = null;
            _Refresh?.Stop();
            _Message = "Session expired or access denied. Log in again.";
        }
        else _Message = message;
    }
    public async ValueTask DisposeAsync()
    {
        if (_Disposed) return;
        _Disposed = true;
        _Route?.Cancel();
        if (_Refresh is not null) await _Refresh.DisposeAsync();
        await Task.WhenAll(_Retired);
        _Route?.Dispose();
    }
}
