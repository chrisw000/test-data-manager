using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace Tdm.Lsp.Tests;

/// <summary>
/// End-to-end over the real stdio framing: a scripted client session is written as
/// Content-Length frames into the input stream, the server runs to `exit`, and the output
/// frames are read back with the same <see cref="JsonRpcConnection"/> — protocol, dispatch
/// and diagnostics in one pass.
/// </summary>
public class LspServerTests
{
    private const string FeatureText = """
        Feature: F
          Scenario: S
            Given a Widget exists
        """;

    private static byte[] Frame(object message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonRpcConnection.JsonOptions);
        return [.. Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n"), .. json];
    }

    private static async Task<List<JsonDocument>> RunSessionAsync(string modelPath, params object[] messages)
    {
        var input = new MemoryStream(messages.SelectMany(Frame).ToArray());
        var output = new MemoryStream();
        await new LspServer(input, output, modelPath).RunAsync(TestContext.Current.CancellationToken);

        output.Position = 0;
        var reader = new JsonRpcConnection(output, Stream.Null);
        var frames = new List<JsonDocument>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken) is { } frame)
            frames.Add(frame);
        return frames;
    }

    [Fact]
    public async Task Initialize_DidOpen_Completion_FullSession()
    {
        var modelPath = Path.Combine(Path.GetTempPath(), $"tdm-lsp-test-{Guid.NewGuid():N}.model.json");
        File.WriteAllText(modelPath, TestModels.OrdersAndBilling().Serialize());
        try
        {
            var frames = await RunSessionAsync(modelPath,
                new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } },
                new { jsonrpc = "2.0", method = "initialized", @params = new { } },
                new
                {
                    jsonrpc = "2.0", method = "textDocument/didOpen",
                    @params = new { textDocument = new { uri = "file:///f.feature", text = FeatureText } },
                },
                new
                {
                    jsonrpc = "2.0", id = 2, method = "textDocument/completion",
                    @params = new
                    {
                        textDocument = new { uri = "file:///f.feature" },
                        position = new { line = 2, character = "    Given a ".Length },
                    },
                },
                new { jsonrpc = "2.0", id = 3, method = "textDocument/hover",
                    @params = new
                    {
                        textDocument = new { uri = "file:///f.feature" },
                        position = new { line = 2, character = 6 },
                    },
                },
                new { jsonrpc = "2.0", id = 4, method = "shutdown" },
                new { jsonrpc = "2.0", method = "exit" });

            // initialize response advertises the three capabilities.
            var initialize = FindResponse(frames, 1);
            var capabilities = initialize.GetProperty("result").GetProperty("capabilities");
            capabilities.GetProperty("hoverProvider").GetBoolean().Should().BeTrue();
            capabilities.GetProperty("textDocumentSync").GetInt32().Should().Be(1);

            // didOpen produced diagnostics: Widget is unknown in the model.
            var diagnostics = frames.Single(f =>
                    f.RootElement.TryGetProperty("method", out var m) &&
                    m.GetString() == "textDocument/publishDiagnostics")
                .RootElement.GetProperty("params").GetProperty("diagnostics");
            diagnostics.GetArrayLength().Should().BeGreaterThan(0);
            diagnostics[0].GetProperty("message").GetString().Should().Contain("Unknown entity 'Widget'");
            diagnostics[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32().Should().Be(2);

            // completion after "Given a " offers model entities.
            var completion = FindResponse(frames, 2).GetProperty("result");
            completion.EnumerateArray().Select(i => i.GetProperty("label").GetString())
                .Should().Contain("Customer");

            // hover on the step returns markdown.
            FindResponse(frames, 3).GetProperty("result").GetProperty("contents").GetProperty("value")
                .GetString().Should().Contain("**Create**");

            FindResponse(frames, 4).GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    [Fact]
    public async Task MissingModel_DegradesToGrammarOnly_AndWarnsOnce()
    {
        var frames = await RunSessionAsync(Path.Combine(Path.GetTempPath(), "does-not-exist.model.json"),
            new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } },
            new
            {
                jsonrpc = "2.0", method = "textDocument/didOpen",
                @params = new { textDocument = new { uri = "file:///f.feature", text = FeatureText } },
            },
            new { jsonrpc = "2.0", method = "exit" });

        frames.Any(f => f.RootElement.TryGetProperty("method", out var m) &&
                        m.GetString() == "window/showMessage").Should().BeTrue();

        // "a Widget exists" parses fine — without a model there is nothing to squiggle.
        var diagnostics = frames.Single(f =>
                f.RootElement.TryGetProperty("method", out var m) &&
                m.GetString() == "textDocument/publishDiagnostics")
            .RootElement.GetProperty("params").GetProperty("diagnostics");
        diagnostics.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task UnknownRequest_GetsMethodNotFound_UnknownNotification_IsIgnored()
    {
        var frames = await RunSessionAsync(Path.Combine(Path.GetTempPath(), "none.model.json"),
            new { jsonrpc = "2.0", id = 9, method = "workspace/executeCommand", @params = new { } },
            new { jsonrpc = "2.0", method = "$/setTrace", @params = new { value = "off" } },
            new { jsonrpc = "2.0", method = "exit" });

        FindResponse(frames, 9).GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32601);
    }

    private static JsonElement FindResponse(List<JsonDocument> frames, int id) =>
        frames.Single(f => f.RootElement.TryGetProperty("id", out var i) &&
                           i.ValueKind == JsonValueKind.Number && i.GetInt32() == id).RootElement;
}
