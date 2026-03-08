using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
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
            var summary = FormatRenameEdit(result, symbolName, newName);
            return applied ? summary : $"(dry-run, edits NOT applied to disk)\n\n{summary}";
        }
        catch (Exception ex) { return FormatError(ex); }
    }

    private static string FormatRenameEdit(JToken result, string oldName, string newName)
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

    [McpServerTool(Name = "format_document"), Description("Format a C# file (or a range of lines) using the project's formatting rules.")]
    public async Task<string> FormatDocument(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Optional start line (1-based) for range formatting")] int? startLine = null,
        [Description("Optional end line (1-based) for range formatting")] int? endLine = null,
        [Description("Tab size. Default: 4")] int tabSize = 4,
        [Description("Use spaces instead of tabs. Default: true")] bool insertSpaces = true,
        CancellationToken ct = default)
    {
        try
        {
            var (uri, err) = await PrepareDocumentRequest(filePath, ct);
            if (uri is null) return err!;

            var options = new JObject
            {
                ["tabSize"] = tabSize,
                ["insertSpaces"] = insertSpaces,
                ["trimTrailingWhitespace"] = true,
                ["insertFinalNewline"] = true,
                ["trimFinalNewlines"] = true,
            };

            JToken? result;
            if (startLine is not null)
            {
                var sl = startLine.Value - 1;
                var el = (endLine ?? startLine.Value) - 1;
                result = await _lsp.RequestAsync("textDocument/rangeFormatting", new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = uri },
                    ["range"] = new JObject
                    {
                        ["start"] = new JObject { ["line"] = sl, ["character"] = 0 },
                        ["end"] = new JObject { ["line"] = el + 1, ["character"] = 0 },
                    },
                    ["options"] = options,
                }, ct);
            }
            else
            {
                result = await _lsp.RequestAsync("textDocument/formatting", new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = uri },
                    ["options"] = options,
                }, ct);
            }

            if (result is not JArray edits || edits.Count == 0)
                return $"No formatting changes needed in {Path.GetFileName(filePath)}.";

            var count = await ApplyTextEditsAsync(filePath, edits, ct);
            var rangeDesc = startLine is not null
                ? $" (lines {startLine}-{endLine ?? startLine})"
                : "";
            return $"Formatted {Path.GetFileName(filePath)}{rangeDesc}: {count} edit(s) applied.";
        }
        catch (Exception ex) { return FormatError(ex); }
    }

    [McpServerTool(Name = "get_workspace_diagnostics"), Description("Get compiler errors and warnings across the entire workspace/solution.")]
    public async Task<string> GetWorkspaceDiagnostics(
        [Description("Optional path filter (case-insensitive). Supports: substring match, wildcards (*\\\\Dune\\\\*.cs), multiple filters separated by ;")] string? pathFilter = null,
        [Description("Minimum severity: error, warning, info, hint. Default: warning")] string? minSeverity = null,
        [Description("Max total diagnostics to return. Default: 200")] int limit = 200,
        CancellationToken ct = default)
    {
        try
        {
            if (RequireSolution() is string err) return err;

            var minSev = (minSeverity?.ToLowerInvariant()) switch
            {
                "error" => 1,
                "warning" or null => 2,
                "info" or "information" => 3,
                "hint" => 4,
                _ => 2,
            };

            var filters = ParsePathFilters(pathFilter);

            JToken? result;
            try
            {
                result = await _lsp.RequestAsync("workspace/diagnostic", new JObject
                {
                    ["previousResultIds"] = new JArray(),
                }, ct);
            }
            catch
            {
                return "workspace/diagnostic not supported by this server. Use get_diagnostics for individual files.";
            }

            var items = result?["items"] as JArray;
            if (items is null || items.Count == 0)
                return "No workspace diagnostics found.";

            var sb = new StringBuilder();
            var totalCount = 0;
            var fileCount = 0;

            foreach (var doc in items)
            {
                var kind = doc["kind"]?.ToString();
                if (kind != "full") continue;

                var docUri = doc["uri"]?.ToString();
                if (docUri is null) continue;

                var path = LspClient.UriToPath(docUri);
                if (filters is not null && !MatchesAnyPathFilter(path, filters))
                    continue;

                var diags = doc["items"] as JArray;
                if (diags is null || diags.Count == 0) continue;

                var fileDiags = new StringBuilder();
                var fileDiagCount = 0;

                foreach (var diag in diags)
                {
                    if (totalCount >= limit) break;

                    var severity = diag["severity"]?.Value<int>() ?? 4;
                    if (severity > minSev) continue;

                    var sevLabel = severity switch { 1 => "Error", 2 => "Warning", 3 => "Info", _ => "Hint" };
                    var line = (diag["range"]?["start"]?["line"]?.Value<int>() ?? 0) + 1;
                    var code = diag["code"]?.ToString() ?? "";
                    var message = diag["message"]?.ToString() ?? "";

                    fileDiags.Append($"    [{sevLabel}] :{line}");
                    if (code.Length > 0) fileDiags.Append($" {code}");
                    fileDiags.AppendLine($": {message}");
                    fileDiagCount++;
                    totalCount++;
                }

                if (fileDiagCount > 0)
                {
                    fileCount++;
                    sb.AppendLine($"  {path} ({fileDiagCount}):");
                    sb.Append(fileDiags);
                }

                if (totalCount >= limit) break;
            }

            if (totalCount == 0)
                return $"No workspace diagnostics at severity '{minSeverity ?? "warning"}' or above" +
                    (pathFilter is not null ? $" matching '{pathFilter}'" : "") + ".";

            var header = $"Found {totalCount} diagnostic(s) across {fileCount} file(s)";
            if (totalCount >= limit) header += $" (limit: {limit})";
            if (pathFilter is not null) header += $" (path: '{pathFilter}')";
            sb.Insert(0, header + ":\n\n");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return FormatError(ex); }
    }
}
