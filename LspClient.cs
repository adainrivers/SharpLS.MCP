using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace SharpLS.MCP;

public class LspClientOptions
{
    public string? SolutionPath { get; set; }
    public string? WorkspaceRoot { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
}

public class LspClient : IHostedService, IDisposable
{
    private readonly LspClientOptions _options;
    private readonly ILogger<LspClient> _logger;
    private Process? _process;
    private JsonRpc? _rpc;
    private readonly HashSet<string> _openDocuments = [];

    public int TimeoutMs => _options.TimeoutSeconds * 1000;

    public LspClient(LspClientOptions options, ILogger<LspClient> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "csharp-ls",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (_options.SolutionPath is not null)
        {
            psi.ArgumentList.Add("--solution");
            psi.ArgumentList.Add(_options.SolutionPath);
        }

        psi.ArgumentList.Add("--loglevel");
        psi.ArgumentList.Add("warning");

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start csharp-ls");

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _logger.LogDebug("[csharp-ls] {Line}", e.Data);
        };
        _process.BeginErrorReadLine();

        var handler = new HeaderDelimitedMessageHandler(
            _process.StandardInput.BaseStream,
            _process.StandardOutput.BaseStream,
            new JsonMessageFormatter());

        _rpc = new JsonRpc(handler);
        _rpc.StartListening();

        await InitializeLspAsync(ct);
    }

    private async Task InitializeLspAsync(CancellationToken ct)
    {
        var workspaceRoot = _options.WorkspaceRoot ?? Directory.GetCurrentDirectory();
        var rootUri = new Uri(workspaceRoot).AbsoluteUri;

        var initParams = new JObject
        {
            ["processId"] = Environment.ProcessId,
            ["rootUri"] = rootUri,
            ["capabilities"] = new JObject
            {
                ["textDocument"] = new JObject
                {
                    ["hover"] = new JObject
                    {
                        ["contentFormat"] = new JArray("markdown", "plaintext")
                    },
                    ["definition"] = new JObject
                    {
                        ["linkSupport"] = true
                    },
                    ["references"] = new JObject(),
                    ["documentSymbol"] = new JObject
                    {
                        ["hierarchicalDocumentSymbolSupport"] = true
                    },
                    ["rename"] = new JObject
                    {
                        ["prepareSupport"] = true
                    },
                    ["diagnostic"] = new JObject(),
                    ["publishDiagnostics"] = new JObject(),
                },
                ["workspace"] = new JObject
                {
                    ["symbol"] = new JObject(),
                    ["workspaceFolders"] = true,
                }
            },
            ["workspaceFolders"] = new JArray(new JObject
            {
                ["uri"] = rootUri,
                ["name"] = Path.GetFileName(workspaceRoot),
            }),
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var result = await _rpc!.InvokeWithParameterObjectAsync<JToken>(
            "initialize", initParams, cts.Token);

        _logger.LogInformation("csharp-ls initialized");

        await _rpc.NotifyWithParameterObjectAsync("initialized", new JObject());

        _logger.LogInformation("LSP initialized notification sent, solution loading in background...");
    }

    public async Task EnsureDocumentOpenAsync(string filePath, CancellationToken ct = default)
    {
        var uri = PathToUri(filePath);
        if (_openDocuments.Contains(uri)) return;

        var text = await File.ReadAllTextAsync(filePath, ct);

        await _rpc!.NotifyWithParameterObjectAsync("textDocument/didOpen", new JObject
        {
            ["textDocument"] = new JObject
            {
                ["uri"] = uri,
                ["languageId"] = "csharp",
                ["version"] = 1,
                ["text"] = text,
            }
        });

        _openDocuments.Add(uri);
    }

    public async Task<JToken?> RequestAsync(string method, JObject @params, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(TimeoutMs));

        return await _rpc!.InvokeWithParameterObjectAsync<JToken>(method, @params, cts.Token);
    }

    public async Task NotifyAsync(string method, JObject @params)
    {
        await _rpc!.NotifyWithParameterObjectAsync(method, @params);
    }

    /// <summary>
    /// Find a symbol's position in a file by searching documentSymbol results.
    /// </summary>
    public async Task<(int line, int character)?> FindSymbolPositionAsync(
        string filePath, string symbolName, string? symbolKind = null, CancellationToken ct = default)
    {
        await EnsureDocumentOpenAsync(filePath, ct);

        var uri = PathToUri(filePath);
        var result = await RequestAsync("textDocument/documentSymbol", new JObject
        {
            ["textDocument"] = new JObject { ["uri"] = uri }
        }, ct);

        if (result is not JArray symbols) return null;

        return SearchSymbolTree(symbols, symbolName, symbolKind);
    }

    /// <summary>
    /// Find ALL positions of a symbol in a file (handles overloads/multiple matches).
    /// </summary>
    public async Task<List<(int line, int character, string name, int kind)>> FindAllSymbolPositionsAsync(
        string filePath, string symbolName, string? symbolKind = null, CancellationToken ct = default)
    {
        await EnsureDocumentOpenAsync(filePath, ct);

        var uri = PathToUri(filePath);
        var result = await RequestAsync("textDocument/documentSymbol", new JObject
        {
            ["textDocument"] = new JObject { ["uri"] = uri }
        }, ct);

        var matches = new List<(int line, int character, string name, int kind)>();
        if (result is JArray symbols)
            CollectMatchingSymbols(symbols, symbolName, symbolKind, matches);

        return matches;
    }

    private static (int line, int character)? SearchSymbolTree(
        JArray symbols, string name, string? kind)
    {
        foreach (var sym in symbols)
        {
            var symName = sym["name"]?.ToString();
            if (string.Equals(symName, name, StringComparison.Ordinal))
            {
                if (kind is not null)
                {
                    var symKind = sym["kind"]?.Value<int>() ?? 0;
                    if (!MatchesKind(symKind, kind)) goto checkChildren;
                }

                var range = sym["selectionRange"] ?? sym["range"];
                if (range?["start"] is JToken start)
                    return (start["line"]!.Value<int>(), start["character"]!.Value<int>());
            }

            checkChildren:
            if (sym["children"] is JArray children)
            {
                var found = SearchSymbolTree(children, name, kind);
                if (found is not null) return found;
            }
        }

        return null;
    }

    private static void CollectMatchingSymbols(
        JArray symbols, string name, string? kind,
        List<(int line, int character, string name, int kind)> matches)
    {
        foreach (var sym in symbols)
        {
            var symName = sym["name"]?.ToString();
            var symKind = sym["kind"]?.Value<int>() ?? 0;

            if (string.Equals(symName, name, StringComparison.Ordinal))
            {
                if (kind is null || MatchesKind(symKind, kind))
                {
                    var range = sym["selectionRange"] ?? sym["range"];
                    if (range?["start"] is JToken start)
                    {
                        matches.Add((
                            start["line"]!.Value<int>(),
                            start["character"]!.Value<int>(),
                            symName!,
                            symKind));
                    }
                }
            }

            if (sym["children"] is JArray children)
                CollectMatchingSymbols(children, name, kind, matches);
        }
    }

    private static bool MatchesKind(int lspKind, string kindStr)
    {
        // LSP SymbolKind enum values
        var expected = kindStr.ToLowerInvariant() switch
        {
            "file" => 1,
            "module" => 2,
            "namespace" => 3,
            "package" => 4,
            "class" => 5,
            "method" => 6,
            "property" => 7,
            "field" => 8,
            "constructor" => 9,
            "enum" => 10,
            "interface" => 11,
            "function" => 12,
            "variable" => 13,
            "constant" => 14,
            "string" => 15,
            "number" => 16,
            "boolean" => 17,
            "array" => 18,
            "object" => 19,
            "key" => 20,
            "null" => 21,
            "enummember" => 22,
            "struct" => 23,
            "event" => 24,
            "operator" => 25,
            "typeparameter" => 26,
            _ => -1,
        };
        return expected == lspKind;
    }

    public static string PathToUri(string filePath)
    {
        return new Uri(filePath).AbsoluteUri;
    }

    public static string UriToPath(string uri)
    {
        return new Uri(uri).LocalPath;
    }

    public static string SymbolKindName(int kind) => kind switch
    {
        1 => "File", 2 => "Module", 3 => "Namespace", 4 => "Package",
        5 => "Class", 6 => "Method", 7 => "Property", 8 => "Field",
        9 => "Constructor", 10 => "Enum", 11 => "Interface",
        12 => "Function", 13 => "Variable", 14 => "Constant",
        22 => "EnumMember", 23 => "Struct", 24 => "Event",
        25 => "Operator", 26 => "TypeParameter",
        _ => $"Unknown({kind})",
    };

    public async Task RestartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Restarting csharp-ls...");
        await StopAsync(ct);
        _openDocuments.Clear();
        await StartAsync(ct);
        _logger.LogInformation("csharp-ls restarted");
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_rpc is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _rpc.InvokeWithParameterObjectAsync<JToken>("shutdown", new JObject(), cts.Token);
                await _rpc.NotifyAsync("exit");
            }
            catch { /* best effort */ }
        }

        _rpc?.Dispose();
        if (_process is { HasExited: false })
        {
            _process.Kill();
        }
        _process?.Dispose();
    }

    public void Dispose()
    {
        _rpc?.Dispose();
        _process?.Dispose();
    }
}
