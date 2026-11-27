namespace ReviewForge.Service;

/// <summary>Circuit-local confirmation and safe feedback shared by legacy rerun entry points.</summary>
public sealed class DashboardConfirmation
{
    private TaskCompletionSource<bool>? _Pending;
    public string? Prompt { get; private set; }
    public string? Message { get; private set; }
    public string? ReturnFocusId { get; private set; }
    public event Action? Changed;

    public async Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken, string? returnFocusId = null)
    {
        if (_Pending is not null) return false;
        var pending = _Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Prompt = prompt;
        ReturnFocusId = returnFocusId;
        Changed?.Invoke();
        try { return await pending.Task.WaitAsync(cancellationToken); }
        finally
        {
            _Pending = null;
            Prompt = null;
            ReturnFocusId = null;
            Changed?.Invoke();
        }
    }

    public void Complete(bool accepted) => _Pending?.TrySetResult(accepted);
    public void Notify(string message) { Message = message; Changed?.Invoke(); }
}
