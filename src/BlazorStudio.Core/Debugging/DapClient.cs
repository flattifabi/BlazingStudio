using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlazorStudio.Core.Debugging;

/// <summary>
/// Minimaler Client für das Debug Adapter Protocol (DAP) über stdin/stdout,
/// z. B. mit netcoredbg (--interpreter=vscode).
/// </summary>
public sealed class DapClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private Stream? _out;
    private int _seq;

    public event Action<string, JsonObject>? EventReceived;

    public event Action<string>? Log;

    public bool IsConnected => _process is { HasExited: false };

    public void Start(string adapterPath, IEnumerable<string> args, string workingDirectory)
    {
        var psi = new ProcessStartInfo(adapterPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        _process = Process.Start(psi) ?? throw new InvalidOperationException("Debug adapter could not be started.");
        _out = _process.StandardInput.BaseStream;
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log?.Invoke(e.Data); };
        _process.BeginErrorReadLine();
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            foreach (var p in _pending.Values) p.TrySetException(new IOException("Debug adapter exited."));
            EventReceived?.Invoke("adapterExited", new JsonObject());
        };
        _ = Task.Run(() => ReadLoop(_process.StandardOutput.BaseStream));
    }

    public async Task<JsonObject> RequestAsync(string command, JsonObject? arguments = null, int timeoutMs = 30000)
    {
        if (_out is null) throw new InvalidOperationException("Not connected.");
        var seq = Interlocked.Increment(ref _seq);
        var msg = new JsonObject
        {
            ["seq"] = seq,
            ["type"] = "request",
            ["command"] = command,
        };
        if (arguments is not null) msg["arguments"] = arguments;

        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = tcs;
        await WriteAsync(msg);

        using var cts = new CancellationTokenSource(timeoutMs);
        await using var reg = cts.Token.Register(() => tcs.TrySetException(new TimeoutException($"DAP request '{command}' timed out.")));
        var response = await tcs.Task;
        if (response["success"]?.GetValue<bool>() != true)
        {
            var message = response["message"]?.GetValue<string>() ?? response["body"]?["error"]?["format"]?.GetValue<string>() ?? "failed";
            throw new DapException(command, message);
        }
        return response["body"] as JsonObject ?? new JsonObject();
    }

    private async Task WriteAsync(JsonObject msg)
    {
        var json = msg.ToJsonString();
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync();
        try
        {
            await _out!.WriteAsync(header);
            await _out.WriteAsync(body);
            await _out.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoop(Stream input)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[16384];
        try
        {
            while (true)
            {
                var n = await input.ReadAsync(chunk);
                if (n <= 0) break;
                buffer.Write(chunk, 0, n);
                while (TryExtractMessage(buffer, out var message))
                    Dispatch(message);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static bool TryExtractMessage(MemoryStream buffer, out string message)
    {
        message = "";
        var data = buffer.GetBuffer();
        var len = (int)buffer.Length;
        var headerEnd = -1;
        for (var i = 0; i + 3 < len; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
            {
                headerEnd = i;
                break;
            }
        }
        if (headerEnd < 0) return false;
        var header = Encoding.ASCII.GetString(data, 0, headerEnd);
        var contentLength = 0;
        foreach (var line in header.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line[15..].Trim());
        }
        var start = headerEnd + 4;
        if (len - start < contentLength) return false;
        message = Encoding.UTF8.GetString(data, start, contentLength);
        var rest = len - start - contentLength;
        var remaining = new byte[rest];
        Array.Copy(data, start + contentLength, remaining, 0, rest);
        buffer.SetLength(0);
        buffer.Write(remaining, 0, rest);
        return true;
    }

    private void Dispatch(string json)
    {
        JsonObject? msg;
        try { msg = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return; }
        if (msg is null) return;

        switch (msg["type"]?.GetValue<string>())
        {
            case "response":
                var reqSeq = msg["request_seq"]?.GetValue<int>() ?? -1;
                if (_pending.TryRemove(reqSeq, out var tcs)) tcs.TrySetResult(msg);
                break;
            case "event":
                var ev = msg["event"]?.GetValue<string>() ?? "";
                EventReceived?.Invoke(ev, msg["body"] as JsonObject ?? new JsonObject());
                break;
            case "request":
                // Reverse requests (runInTerminal) beantworten wir mit Fehler → Adapter startet selbst.
                var seq = msg["seq"]?.GetValue<int>() ?? 0;
                _ = WriteAsync(new JsonObject
                {
                    ["seq"] = Interlocked.Increment(ref _seq), ["type"] = "response", ["request_seq"] = seq,
                    ["command"] = msg["command"]?.GetValue<string>(), ["success"] = false, ["message"] = "not supported",
                });
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (IsConnected) await RequestAsync("disconnect", new JsonObject { ["terminateDebuggee"] = true }, 3000);
        }
        catch { }
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
        _process?.Dispose();
        _process = null;
        _out = null;
    }
}

public sealed class DapException(string command, string message) : Exception($"{command}: {message}");
