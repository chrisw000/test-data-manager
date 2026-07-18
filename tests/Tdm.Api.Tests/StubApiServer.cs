using System.Net;
using System.Text;

namespace Tdm.Api.Tests;

/// <summary>
/// Minimal in-process stub HTTP API (the WireMock role in the wave-4 acceptance criteria,
/// §5 — dependency-free): records every request and answers via a test-supplied handler.
/// </summary>
public sealed class StubApiServer : IDisposable
{
    public sealed record RecordedRequest(string Method, string PathAndQuery, string Body, string? Authorization);

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Lock _sync = new();
    private readonly List<RecordedRequest> _requests = [];

    /// <summary>Handler for each request; default answers 404. Set per test.</summary>
    public Func<RecordedRequest, (int Status, string? Body)> OnRequest { get; set; } = _ => (404, null);

    public string BaseUrl { get; }

    public IReadOnlyList<RecordedRequest> Requests { get { lock (_sync) return [.. _requests]; } }

    public StubApiServer()
    {
        // HttpListener cannot bind port 0 — probe for a free one.
        var random = new Random();
        for (var attempt = 0; ; attempt++)
        {
            var port = random.Next(20_000, 60_000);
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                _listener.Start();
                BaseUrl = $"http://127.0.0.1:{port}";
                break;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                // Port in use — try another.
            }
        }
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) { return; } // listener stopped

            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync().ConfigureAwait(false);

            var recorded = new RecordedRequest(
                context.Request.HttpMethod,
                context.Request.Url!.PathAndQuery,
                body,
                context.Request.Headers["Authorization"] ?? context.Request.Headers["X-Api-Key"]);
            lock (_sync) _requests.Add(recorded);

            var (status, responseBody) = OnRequest(recorded);
            context.Response.StatusCode = status;
            if (responseBody is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(responseBody);
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            }
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* loop faulted on stop */ }
        _listener.Close();
    }
}
