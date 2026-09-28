namespace ReviewForge.Testing;

/// <summary>Scripted HTTP handler shared by adapter tests.</summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly object _Gate = new();
    private readonly Queue<Func<HttpRequestMessage, int, HttpResponseMessage>> _Script = new();

    public StubHttpMessageHandler()
    {
    }

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => Enqueue(respond);

    public StubHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        => Enqueue(respond);

    public StubHttpMessageHandler(Func<HttpResponseMessage> respond)
        => Enqueue(_ => respond());

    public List<HttpRequestMessage> Requests { get; } = [];

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => _Script.Enqueue((request, _) => respond(request));

    public void Enqueue(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        => _Script.Enqueue(respond);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Func<HttpRequestMessage, int, HttpResponseMessage> respond;
        int requestNumber;
        lock (_Gate)
        {
            Requests.Add(request);
            requestNumber = Requests.Count;
            respond = _Script.Count == 0
                ? throw new InvalidOperationException("StubHttpMessageHandler: no scripted response left.")
                : _Script.Dequeue();
        }

        return Task.FromResult(respond(request, requestNumber));
    }
}
