using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ModularFramework;

/// <summary>
/// JSON-RPC 2.0 qua Named Pipe (duplex). ModuleHost = pipe SERVER, host = pipe CLIENT.
/// Cả 2 phía đều gửi được request lẫn response (pipe duplex).
/// </summary>
public sealed class JsonRpcChannel : IDisposable
{
    private readonly PipeStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _nextId = 1;
    // pending giữ RAW JSON (string) — không giữ JsonElement rẻ tiền có thể chết theo document
    private readonly Dictionary<long, TaskCompletionSource<string>> _pending = new();
    public Func<string, JsonElement, CancellationToken, Task<JsonElement>>? OnRequest { get; set; }

    // camelCase trên wire (JSON-RPC chuẩn), case-insensitive khi đọc
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public JsonRpcChannel(PipeStream stream) => _stream = stream;

    public static async Task<JsonRpcChannel> WaitForHostAsync(string pipeName, CancellationToken ct)
    {
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(ct);
        return new JsonRpcChannel(server);
    }

    public static async Task<JsonRpcChannel> ConnectToModuleAsync(string pipeName, CancellationToken ct)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(ct);
        return new JsonRpcChannel(client);
    }

    /// <summary>Gửi request, chờ response (timeout qua ct).</summary>
    public async Task<JsonElement> CallAsync(string method, JsonElement? args = null, CancellationToken ct = default)
    {
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;

        var req = new JsonRpcRequest { Id = id, Method = method, Params = args ?? JsonSerializer.SerializeToElement(new { }) };
        await SendAsync(JsonSerializer.SerializeToUtf8Bytes(req, JsonOpts), ct);

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        var raw = await tcs.Task;
        using var doc = JsonDocument.Parse(raw);   // parse lại — document sống đủ lâu
        var resp = doc.RootElement;
        if (resp.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
            throw new JsonRpcException(err.GetProperty("message").GetString() ?? "rpc error");
        return resp.TryGetProperty("result", out var result) ? result.Clone() : JsonSerializer.SerializeToElement(new { });
    }

    /// <summary>Gửi notification (không cần response).</summary>
    public async Task NotifyAsync(string method, JsonElement? args = null, CancellationToken ct = default)
    {
        var n = new JsonRpcRequest { Id = null, Method = method, Params = args ?? JsonSerializer.SerializeToElement(new { }) };
        await SendAsync(JsonSerializer.SerializeToUtf8Bytes(n, JsonOpts), ct);
    }

    /// <summary>Vòng đọc: nhận request → OnRequest → trả response; hoặc response → giải phóng pending.</summary>
    public async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var ms = new MemoryStream();
        while (!ct.IsCancellationRequested)
        {
            int n = await _stream.ReadAsync(buffer, ct);
            if (n <= 0) break; // đối phương đóng pipe
            ms.Write(buffer, 0, n);
            await ProcessMessagesAsync(ms, ct);
        }
    }

    /// <summary>
    /// Tách message theo '\n' và xử lý TỪNG message TRONG scope của JsonDocument —
    /// document dispose NGAY SAU khi xử lý xong → không bao giờ có element treo.
    /// </summary>
    private async Task ProcessMessagesAsync(MemoryStream ms, CancellationToken ct)
    {
        var bytes = ms.ToArray();
        int start = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            var slice = bytes[start..i];
            start = i + 1;
            if (slice.Length == 0) continue;
            using var doc = JsonDocument.Parse(slice);
            await HandleMessageAsync(doc.RootElement, ct);
        }
        ms.SetLength(0);
        ms.Position = 0;
        if (start < bytes.Length) ms.Write(bytes, start, bytes.Length - start);
    }

    private async Task HandleMessageAsync(JsonElement msg, CancellationToken ct)
    {
        bool hasId = msg.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number;
        long id = 0;
        if (hasId) idEl.TryGetInt64(out id);
        if (hasId && msg.TryGetProperty("method", out _))
        {
            // request từ đối phương
            var method = msg.GetProperty("method").GetString() ?? "";
            var prms = msg.TryGetProperty("params", out var p) ? p : JsonSerializer.SerializeToElement(new { });
            JsonRpcResponse resp;
            try
            {
                var result = OnRequest != null
                    ? await OnRequest(method, prms, ct)
                    : JsonSerializer.SerializeToElement(new { error = "no handler" });
                resp = new JsonRpcResponse { Id = id, Result = result };
            }
            catch (Exception ex)
            {
                // BẮT BUỘC set Result khác default — serialize default JsonElement sẽ ném
                resp = new JsonRpcResponse
                {
                    Id = id,
                    Result = JsonSerializer.SerializeToElement(new { }),
                    Error = new JsonRpcError { Code = -32000, Message = ex.Message },
                };
            }
            await SendAsync(JsonSerializer.SerializeToUtf8Bytes(resp, JsonOpts), ct);
        }
        else if (hasId)
        {
            // response cho request của mình — lưu RAW TEXT (element sẽ chết theo document)
            TaskCompletionSource<string>? tcs = null;
            lock (_pending) { if (_pending.Remove(id, out var t)) tcs = t; }
            tcs?.TrySetResult(msg.GetRawText());
        }
        // notification (không id): bỏ qua ở scaffold
    }

    private async Task SendAsync(byte[] payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var framed = payload.Concat(new byte[] { (byte)'\n' }).ToArray();
            await _stream.WriteAsync(framed, ct);
            await _stream.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { }
        lock (_pending) foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
        _pending.Clear();
    }
}

public sealed class JsonRpcRequest
{
    public long? Id { get; set; }
    public required string Method { get; set; }
    public JsonElement Params { get; set; }
}

public sealed class JsonRpcResponse
{
    public long Id { get; set; }
    public JsonElement Result { get; set; }
    public JsonRpcError? Error { get; set; }
}

public sealed class JsonRpcError
{
    public int Code { get; set; }
    public required string Message { get; set; }
}

public sealed class JsonRpcException : Exception
{
    public JsonRpcException(string message) : base(message) { }
}
