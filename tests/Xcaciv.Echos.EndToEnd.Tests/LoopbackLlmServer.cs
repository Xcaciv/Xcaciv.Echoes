using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Xcaciv.Echos.EndToEnd.Tests;

internal sealed class LoopbackLlmServer : IDisposable
{
    private const int MaxBodyBytes = 1024 * 1024;
    private readonly HttpListener _listener;
    private readonly List<LoopbackRequest> _requests = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public LoopbackLlmServer()
    {
        var attempt = 0;
        while (true)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException) when (++attempt < 5)
            {
                listener.Close();
                continue;
            }
            _listener = listener;
            Endpoint = new Uri("http://127.0.0.1:" + port + "/v1/chat/completions");
            break;
        }
        _loop = Task.Run(AcceptLoopAsync);
    }

    public Uri Endpoint { get; }

    public bool HoldResponses { get; set; }

    public int RequestCount
    {
        get
        {
            lock (_gate)
                return _requests.Count;
        }
    }

    public IReadOnlyList<LoopbackRequest> Requests
    {
        get
        {
            lock (_gate)
                return _requests.ToList();
        }
    }

    public void ReleaseResponses() => _release.TrySetResult();

    public async Task WaitForRequestsAsync(int count, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (RequestCount < count)
        {
            if (clock.Elapsed >= timeout)
                throw new TimeoutException("Expected " + count + " request(s) but only " + RequestCount + " arrived.");
            await Task.Delay(100);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _release.TrySetResult();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        _loop.Wait(TimeSpan.FromSeconds(5));
        _stop.Dispose();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var body = await ReadBodyAsync(request);
            lock (_gate)
                _requests.Add(new LoopbackRequest(request.HttpMethod, request.Url?.AbsolutePath ?? string.Empty,
                    request.Headers["Authorization"] is not null, body));
            if (HoldResponses)
                await _release.Task.WaitAsync(_stop.Token);
            if (request.Url?.AbsolutePath != "/v1/chat/completions")
            {
                context.Response.StatusCode = 404;
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(LlmStub.ResponseJson);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, _stop.Token);
            }
            context.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException || ex is ObjectDisposedException
            || ex is OperationCanceledException || ex is InvalidOperationException || ex is IOException)
        {
        }
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                break;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
