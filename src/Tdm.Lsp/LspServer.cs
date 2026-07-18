using System.Text.Json;
using Tdm.Core.Model;

namespace Tdm.Lsp;

/// <summary>
/// The TDM language server (W4-D2): stdio LSP with full-document sync, publishing
/// diagnostics from the *actual* <c>StepGrammar</c> (no reimplementation) validated against
/// the exported <c>tdm.model.json</c>, plus completion and hover. The model file is
/// re-read whenever its timestamp changes, so `tdm export-model` takes effect live; with no
/// model file the server degrades to grammar-only diagnostics.
/// </summary>
public sealed class LspServer(Stream input, Stream output, string modelPath)
{
    private readonly JsonRpcConnection _rpc = new(input, output);
    private readonly Dictionary<string, string> _documents = [];
    private TdmModel? _model;
    private DateTime _modelTimestamp;
    private bool _missingModelReported;

    public async Task RunAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            using var message = await _rpc.ReadAsync(ct).ConfigureAwait(false);
            if (message is null) return; // client closed the pipe
            var root = message.RootElement;
            if (!root.TryGetProperty("method", out var methodElement)) continue; // stray response
            var method = methodElement.GetString() ?? "";
            var hasId = root.TryGetProperty("id", out var idElement);
            var id = hasId ? idElement.Clone() : default;
            root.TryGetProperty("params", out var p);

            try
            {
                switch (method)
                {
                    case "initialize":
                        await _rpc.WriteResponseAsync(id, new
                        {
                            capabilities = new
                            {
                                textDocumentSync = 1, // full-document sync: feature files are small
                                completionProvider = new { triggerCharacters = new[] { " ", "@", "\"" } },
                                hoverProvider = true,
                            },
                            serverInfo = new { name = "tdm-lsp", version = ModelVersion() },
                        }, ct).ConfigureAwait(false);
                        await ReportMissingModelOnceAsync(ct).ConfigureAwait(false);
                        break;

                    case "shutdown":
                        await _rpc.WriteResponseAsync(id, null, ct).ConfigureAwait(false);
                        break;

                    case "exit":
                        return;

                    case "textDocument/didOpen":
                    {
                        var doc = p.GetProperty("textDocument");
                        var uri = doc.GetProperty("uri").GetString()!;
                        _documents[uri] = doc.GetProperty("text").GetString() ?? "";
                        await PublishDiagnosticsAsync(uri, ct).ConfigureAwait(false);
                        break;
                    }

                    case "textDocument/didChange":
                    {
                        var uri = p.GetProperty("textDocument").GetProperty("uri").GetString()!;
                        var changes = p.GetProperty("contentChanges");
                        // Full sync — the last change carries the whole document.
                        var last = changes[changes.GetArrayLength() - 1];
                        _documents[uri] = last.GetProperty("text").GetString() ?? "";
                        await PublishDiagnosticsAsync(uri, ct).ConfigureAwait(false);
                        break;
                    }

                    case "textDocument/didClose":
                    {
                        var uri = p.GetProperty("textDocument").GetProperty("uri").GetString()!;
                        _documents.Remove(uri);
                        await _rpc.WriteNotificationAsync("textDocument/publishDiagnostics",
                            new { uri, diagnostics = Array.Empty<object>() }, ct).ConfigureAwait(false);
                        break;
                    }

                    case "textDocument/completion":
                    {
                        var uri = p.GetProperty("textDocument").GetProperty("uri").GetString()!;
                        var position = p.GetProperty("position");
                        var items = _documents.TryGetValue(uri, out var text)
                            ? CompletionProvider.Complete(text,
                                position.GetProperty("line").GetInt32(),
                                position.GetProperty("character").GetInt32(), Model())
                            : [];
                        await _rpc.WriteResponseAsync(id, items.Select(i => new
                        {
                            label = i.Label,
                            kind = i.Kind,
                            detail = i.Detail,
                            insertText = i.InsertText,
                        }), ct).ConfigureAwait(false);
                        break;
                    }

                    case "textDocument/hover":
                    {
                        var uri = p.GetProperty("textDocument").GetProperty("uri").GetString()!;
                        var line = p.GetProperty("position").GetProperty("line").GetInt32();
                        string? markdown = null;
                        if (_documents.TryGetValue(uri, out var text))
                        {
                            var lines = text.ReplaceLineEndings("\n").Split('\n');
                            if (line < lines.Length)
                                markdown = HoverProvider.Hover(lines[line], Model());
                        }
                        await _rpc.WriteResponseAsync(id,
                            markdown is null ? null : new { contents = new { kind = "markdown", value = markdown } },
                            ct).ConfigureAwait(false);
                        break;
                    }

                    default:
                        if (hasId) // unknown *request* needs an answer; unknown notifications are ignorable
                            await _rpc.WriteErrorAsync(id, -32601, $"Method not found: {method}", ct).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (hasId)
                    await _rpc.WriteErrorAsync(id, -32603, ex.Message, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task PublishDiagnosticsAsync(string uri, CancellationToken ct)
    {
        var diagnostics = FeatureLint.Analyze(_documents[uri], Model());
        await _rpc.WriteNotificationAsync("textDocument/publishDiagnostics", new
        {
            uri,
            diagnostics = diagnostics.Select(d => new
            {
                range = new
                {
                    start = new { line = d.Line, character = d.StartChar },
                    end = new { line = d.Line, character = d.EndChar },
                },
                severity = d.Severity,
                source = "tdm",
                message = d.Message,
            }),
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Cached model, re-read when the file's timestamp changes — `tdm export-model`
    /// output takes effect without restarting the editor.</summary>
    private TdmModel? Model()
    {
        if (!File.Exists(modelPath)) return _model = null;
        var timestamp = File.GetLastWriteTimeUtc(modelPath);
        if (_model is not null && timestamp == _modelTimestamp) return _model;
        try
        {
            _model = TdmModel.Load(modelPath);
            _modelTimestamp = timestamp;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Mid-write or malformed — keep the previous model; the next change re-reads.
        }
        return _model;
    }

    private async Task ReportMissingModelOnceAsync(CancellationToken ct)
    {
        if (Model() is not null || _missingModelReported) return;
        _missingModelReported = true;
        await _rpc.WriteNotificationAsync("window/showMessage", new
        {
            type = 2, // warning
            message = $"TDM model file not found at '{modelPath}' — entity/property validation is off. " +
                      "Generate it with: tdm export-model",
        }, ct).ConfigureAwait(false);
    }

    private static string ModelVersion() =>
        typeof(LspServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
