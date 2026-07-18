using System.Text;
using System.Text.Json;

namespace Tdm.Lsp;

/// <summary>
/// LSP base-protocol framing over a stream pair: `Content-Length: N\r\n\r\n{json}` in both
/// directions. Hand-rolled (~100 lines) rather than a server framework — the TDM server
/// speaks six methods, and the dotnet tool stays lean (W4-D3).
/// </summary>
public sealed class JsonRpcConnection(Stream input, Stream output)
{
    /// <summary>LSP JSON is camelCase; nulls omitted so optional fields stay absent.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads one framed message; null at end-of-stream (client closed the pipe).</summary>
    public async Task<JsonDocument?> ReadAsync(CancellationToken ct = default)
    {
        var contentLength = -1;
        while (true)
        {
            var header = await ReadHeaderLineAsync(ct).ConfigureAwait(false);
            if (header is null) return null;
            if (header.Length == 0)
            {
                if (contentLength >= 0) break; // blank line ends the header block
                continue; // stray blank line before any header — tolerate and keep reading
            }
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(header["Content-Length:".Length..].Trim());
        }

        var buffer = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = await input.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }
        return JsonDocument.Parse(buffer);
    }

    /// <summary>Reads a \r\n-terminated header line as ASCII; null at end-of-stream.
    /// Byte-at-a-time is fine — headers are ~30 bytes and arrive per message.</summary>
    private async Task<string?> ReadHeaderLineAsync(CancellationToken ct)
    {
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var n = await input.ReadAsync(one.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0) return line.Length == 0 ? null : line.ToString();
            var c = (char)one[0];
            if (c == '\n')
            {
                if (line.Length > 0 && line[^1] == '\r') line.Length--;
                return line.ToString();
            }
            line.Append(c);
        }
    }

    /// <summary>A dictionary, not an anonymous type: `result` must be present even when null
    /// (JSON-RPC requires it), and <see cref="JsonOptions"/> strips null *properties*.</summary>
    public Task WriteResponseAsync(JsonElement id, object? result, CancellationToken ct = default) =>
        WriteAsync(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }, ct);

    public Task WriteErrorAsync(JsonElement id, int code, string message, CancellationToken ct = default) =>
        WriteAsync(new { jsonrpc = "2.0", id, error = new { code, message } }, ct);

    public Task WriteNotificationAsync(string method, object @params, CancellationToken ct = default) =>
        WriteAsync(new { jsonrpc = "2.0", method, @params }, ct);

    private async Task WriteAsync(object message, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n");
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        await output.WriteAsync(json, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }
}
