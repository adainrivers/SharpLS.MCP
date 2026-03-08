using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace SharpLS.MCP;

[McpServerToolType]
public class EditTools : ToolBase
{
    public EditTools(LspClient lsp) : base(lsp) { }

    [McpServerTool(Name = "get_diagnostics"), Description("Get compiler errors and warnings for a C# file without building.")]
    public async Task<string> GetDiagnostics(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Minimum severity to include: error, warning, info, hint. Default: warning")] string? minSeverity = null,
        CancellationToken ct = default)
    {
        try
        {
            var (uri, err) = await PrepareDocumentRequest(filePath, ct);
            if (uri is null) return err!;

            var result = await _lsp.RequestAsync("textDocument/diagnostic", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri },
            }, ct);

            var items = result?["items"] as JArray;
            if (items is null || items.Count == 0)
                return $"No diagnostics found in {Path.GetFileName(filePath)}.";

            var minSev = (minSeverity?.ToLowerInvariant()) switch
            {
                "error" => 1,
                "warning" or null => 2,
                "info" or "information" => 3,
                "hint" => 4,
                _ => 2,
            };

            var sb = new StringBuilder();
            var count = 0;

            foreach (var diag in items)
            {
                var severity = diag["severity"]?.Value<int>() ?? 4;
                if (severity > minSev) continue;

                var sevLabel = severity switch { 1 => "Error", 2 => "Warning", 3 => "Info", _ => "Hint" };
                var line = (diag["range"]?["start"]?["line"]?.Value<int>() ?? 0) + 1;
                var code = diag["code"]?.ToString() ?? "";
                var message = diag["message"]?.ToString() ?? "";

                sb.Append($"  [{sevLabel}] {Path.GetFileName(filePath)}:{line}");
                if (code.Length > 0) sb.Append($" {code}");
                sb.AppendLine($": {message}");
                count++;
            }

            if (count == 0)
                return $"No diagnostics at severity '{minSeverity ?? "warning"}' or above in {Path.GetFileName(filePath)}.";

            sb.Insert(0, $"Found {count} diagnostic(s) in {Path.GetFileName(filePath)}:\n\n");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return FormatError(ex); }
    }

    [McpServerTool(Name = "rename_symbol"), Description("Rename a symbol across the workspace. Returns a preview of all changes that would be made.")]
    public async Task<string> RenameSymbol(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the symbol to rename")] string symbolName,
        [Description("New name for the symbol")] string newName,
        [Description("Kind of symbol: class, method, property, field, interface, enum, struct, etc.")] string? symbolKind = null,
        CancellationToken ct = default)
    {
        try
        {
            var (ctx, err) = await PrepareSymbolRequest(filePath, symbolName, symbolKind, ct);
            if (ctx is null) return err!;

            var prepareResult = await _lsp.RequestAsync("textDocument/prepareRename", MakePositionParams(ctx), ct);
            if (prepareResult is null)
                return $"Symbol '{symbolName}' cannot be renamed.";

            var renameParams = MakePositionParams(ctx);
            renameParams["newName"] = newName;

            var result = await _lsp.RequestAsync("textDocument/rename", renameParams, ct);

            if (result is null)
                return $"Rename failed - no changes returned.";

            var applied = await ApplyWorkspaceEditAsync(result, ct);
            var summary = FormatWorkspaceEdit(result, symbolName, newName);
            return applied ? summary : $"(dry-run, edits NOT applied to disk)\n\n{summary}";
        }
        catch (Exception ex) { return FormatError(ex); }
    }

    // -- Workspace edit application --

    private async Task<bool> ApplyWorkspaceEditAsync(JToken result, CancellationToken ct)
    {
        var fileEdits = new Dictionary<string, List<(int startLine, int startChar, int endLine, int endChar, string newText)>>();

        void CollectEdits(string uri, JArray edits)
        {
            var path = LspClient.UriToPath(uri);
            if (!fileEdits.TryGetValue(path, out var list))
                fileEdits[path] = list = [];

            foreach (var edit in edits)
            {
                var range = edit["range"];
                if (range is null) continue;
                list.Add((
                    range["start"]!["line"]!.Value<int>(),
                    range["start"]!["character"]!.Value<int>(),
                    range["end"]!["line"]!.Value<int>(),
                    range["end"]!["character"]!.Value<int>(),
                    edit["newText"]?.ToString() ?? ""));
            }
        }

        if (result["documentChanges"] is JArray docChanges)
        {
            foreach (var dc in docChanges)
            {
                var uri = dc["textDocument"]?["uri"]?.ToString();
                if (uri is not null && dc["edits"] is JArray edits)
                    CollectEdits(uri, edits);
            }
        }

        if (result["changes"] is JObject changes)
        {
            foreach (var prop in changes.Properties())
            {
                if (prop.Value is JArray edits)
                    CollectEdits(prop.Name, edits);
            }
        }

        if (fileEdits.Count == 0) return false;

        foreach (var (path, edits) in fileEdits)
        {
            var text = await File.ReadAllTextAsync(path, ct);
            var lines = text.Split('\n');

            foreach (var (sl, sc, el, ec, newText) in edits.OrderByDescending(e => e.startLine).ThenByDescending(e => e.startChar))
            {
                var startOffset = GetOffset(lines, sl, sc);
                var endOffset = GetOffset(lines, el, ec);
                text = string.Concat(text.AsSpan(0, startOffset), newText, text.AsSpan(endOffset));
                lines = text.Split('\n');
            }

            await File.WriteAllTextAsync(path, text, ct);

            var uri = LspClient.PathToUri(path);
            await _lsp.NotifyAsync("textDocument/didClose", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri }
            });
            _lsp.MarkDocumentClosed(uri);
        }

        return true;
    }

    private static int GetOffset(string[] lines, int line, int character)
    {
        var offset = 0;
        for (var i = 0; i < line && i < lines.Length; i++)
            offset += lines[i].Length + 1;
        return offset + character;
    }

    private static string FormatWorkspaceEdit(JToken result, string oldName, string newName)
    {
        var sb = new StringBuilder();
        var totalEdits = 0;
        var fileCount = 0;

        void FormatChanges(string docUri, JArray edits)
        {
            var path = LspClient.UriToPath(docUri);
            if (edits.Count == 0) return;

            fileCount++;
            sb.AppendLine($"  {path} ({edits.Count} edit(s))");
            foreach (var edit in edits)
            {
                var line = (edit["range"]?["start"]?["line"]?.Value<int>() ?? 0) + 1;
                sb.AppendLine($"    line {line}: '{oldName}' -> '{newName}'");
                totalEdits++;
            }
        }

        if (result["documentChanges"] is JArray docChanges)
        {
            foreach (var dc in docChanges)
            {
                var uri = dc["textDocument"]?["uri"]?.ToString();
                if (uri is not null && dc["edits"] is JArray edits)
                    FormatChanges(uri, edits);
            }
        }

        if (result["changes"] is JObject changes)
        {
            foreach (var prop in changes.Properties())
            {
                if (prop.Value is JArray edits)
                    FormatChanges(prop.Name, edits);
            }
        }

        if (totalEdits == 0)
            return $"Rename '{oldName}' -> '{newName}': no changes needed.";

        sb.Insert(0, $"Rename '{oldName}' -> '{newName}': {totalEdits} edit(s) across {fileCount} file(s):\n\n");
        return sb.ToString().TrimEnd();
    }
}
