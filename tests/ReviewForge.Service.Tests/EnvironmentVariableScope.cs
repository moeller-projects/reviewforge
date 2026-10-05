namespace ReviewForge.Service.Tests;

/// <summary>Temporarily overrides process environment variables and restores their prior values.</summary>
internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly Dictionary<string, string?> _PreviousValues = new(StringComparer.Ordinal);
    private bool _Disposed;

    public void Set(string name, string? value)
    {
        ObjectDisposedException.ThrowIf(_Disposed, this);
        _PreviousValues.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    public void SetIfUnset(string name, string value)
    {
        if (Environment.GetEnvironmentVariable(name) is null)
        {
            Set(name, value);
        }
    }

    public void Dispose()
    {
        if (_Disposed)
        {
            return;
        }

        _Disposed = true;
        foreach (var (name, value) in _PreviousValues)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
