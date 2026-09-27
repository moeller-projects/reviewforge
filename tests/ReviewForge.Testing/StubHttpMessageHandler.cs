using System.Net;

namespace ReviewForge.Testing;

/// <summary>Scripted HTTP handler shared by adapter tests.</summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _Script = new();

    public StubHttpMessageHandler()
    {
    }

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => Enqueue(respond);

    public StubHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        => Enqueue(request => respond(request, Requests.Count));

    public StubHttpMessageHandler(Func<HttpResponseMessage> respond)
        => Enqueue(_ => respond());

    public List<HttpRequestMessage> Requests { get; } = [];

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => _Script.Enqueue(respond);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return _Script.Count == 0
            ? throw new InvalidOperationException("StubHttpMessageHandler: no scripted response left.")
            : Task.FromResult(_Script.Dequeue()(request));
    }
}
