using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace SharpLS.MCP;

public abstract class ToolBase
{
    protected readonly LspClient _lsp;

    protected ToolBase(LspClient lsp) => _lsp = lsp;

    protected record SymbolContext(string Uri, int Line, int Character);

    protected string? RequireSolution()
    {
        if (!_lsp.IsSolutionLoaded)
            return "No solution loaded. Call load_solution first.";
        return null;
    }

    protected async Task<(SymbolContext? Ctx, string? Error)> PrepareSymbolRequest(
        string filePath, string symbolName, string? symbolKind, CancellationToken ct)
    {
        if (RequireSolution() is string err) return (null, err);
        var pos = await _lsp.FindSymbolPositionAsync(filePath, symbolName, symbolKind, ct);
        if (pos is null) return (null, $"Symbol '{symbolName}' not found in {filePath}");
        await _lsp.EnsureDocumentOpenAsync(filePath, ct);
        var uri = LspClient.PathToUri(filePath);
        return (new SymbolContext(uri, pos.Value.line, pos.Value.character), null);
    }

    protected async Task<(string? Uri, string? Error)> PrepareDocumentRequest(
        string filePath, CancellationToken ct)
    {
        if (RequireSolution() is string err) return (null, err);
        await _lsp.EnsureDocumentOpenAsync(filePath, ct);
        return (LspClient.PathToUri(filePath), null);
    }

    protected static JObject MakePositionParams(SymbolContext ctx) => new()
    {
        ["textDocument"] = new JObject { ["uri"] = ctx.Uri },
        ["position"] = new JObject { ["line"] = ctx.Line, ["character"] = ctx.Character },
    };

    // -- Response formatting --

    protected static string FormatFilteredHeader(
        string prefix, int shown, int matched, int total, string? filter)
    {
        var sb = new StringBuilder(prefix);
        if (filter is not null)
        {
            sb.Append(shown < matched
                ? $"{shown} of {matched} matching '{filter}'"
                : $"{matched} matching '{filter}'");
            sb.Append($", {total} total");
        }
        else
        {
            sb.Append(shown < total ? $"{shown} of {total}" : $"{total}");
        }
        sb.Append("):");
        return sb.ToString();
    }

    protected static string FormatHierarchyItem(JToken item)
    {
        var name = item["name"]?.ToString() ?? "?";
        var kind = LspClient.SymbolKindName(item["kind"]?.Value<int>() ?? 0);
        var uri = item["uri"]?.ToString();
        var path = uri is not null ? LspClient.UriToPath(uri) : "?";
        var range = item["selectionRange"] ?? item["range"];
        var line = (range?["start"]?["line"]?.Value<int>() ?? 0) + 1;
        return $"  {name} ({kind}) - {path}:{line}";
    }

    protected static string FormatLocations(JToken? result, string label, string? filter = null, int limit = 0)
    {
        if (result is null) return $"No {label} found.";

        var locations = new List<(string path, int line, int col)>();

        if (result is JArray arr)
        {
            foreach (var item in arr)
                ExtractLocation(item, locations);
        }
        else
        {
            ExtractLocation(result, locations);
        }

        if (locations.Count == 0) return $"No {label} found.";

        var filterRegex = filter is not null ? new Regex(filter, RegexOptions.IgnoreCase) : null;
        var sb = new StringBuilder();
        var shown = 0;
        var matched = 0;
        var total = locations.Count;
        var fileLineCache = new Dictionary<string, string[]>();

        foreach (var (path, line, col) in locations)
        {
            var symbolName = ExtractSymbolNameFromFile(path, line, fileLineCache);
            var matchTarget = symbolName ?? path;
            if (filterRegex is not null && !filterRegex.IsMatch(matchTarget)) continue;
            matched++;
            if (limit > 0 && shown >= limit) continue;

            if (symbolName is not null)
                sb.AppendLine($"  {symbolName} - {path}:{line}");
            else
                sb.AppendLine($"  {path}:{line}:{col}");
            shown++;
        }

        if (shown == 0) return $"No {label} found" + (filter is not null ? $" matching '{filter}'." : ".");

        sb.Insert(0, FormatFilteredHeader($"Found ", shown, matched, total, filter) + "\n\n");
        return sb.ToString().TrimEnd();
    }

    protected static string FormatError(Exception ex)
    {
        if (ex is OperationCanceledException)
            return "Request timed out. The language server may still be loading the solution. Try again in a moment.";

        var sb = new StringBuilder();
        sb.AppendLine($"Error: {ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException is not null)
            sb.AppendLine($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        return sb.ToString().TrimEnd();
    }

    protected static void FormatSymbolTree(JArray symbols, StringBuilder sb, int indent)
    {
        foreach (var sym in symbols)
        {
            var name = sym["name"]?.ToString() ?? "?";
            var kind = LspClient.SymbolKindName(sym["kind"]?.Value<int>() ?? 0);
            var range = sym["selectionRange"] ?? sym["range"];
            var line = (range?["start"]?["line"]?.Value<int>() ?? 0) + 1;

            sb.Append(new string(' ', indent * 2));
            sb.AppendLine($"{name} ({kind}) line {line}");

            if (sym["children"] is JArray children)
                FormatSymbolTree(children, sb, indent + 1);
        }
    }

    // -- Location extraction --

    protected static void ExtractLocation(JToken item, List<(string path, int line, int col)> locations)
    {
        if (item["uri"] is JToken uri && item["range"] is JToken range)
        {
            var path = LspClient.UriToPath(uri.ToString());
            var line = (range["start"]?["line"]?.Value<int>() ?? 0) + 1;
            var col = (range["start"]?["character"]?.Value<int>() ?? 0) + 1;
            locations.Add((path, line, col));
        }
        else if (item["targetUri"] is JToken targetUri && item["targetRange"] is JToken targetRange)
        {
            var path = LspClient.UriToPath(targetUri.ToString());
            var line = (targetRange["start"]?["line"]?.Value<int>() ?? 0) + 1;
            var col = (targetRange["start"]?["character"]?.Value<int>() ?? 0) + 1;
            locations.Add((path, line, col));
        }
    }

    protected static string? ExtractSymbolNameFromFile(string path, int line, Dictionary<string, string[]> cache)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (!cache.TryGetValue(path, out var lines))
                cache[path] = lines = File.ReadAllLines(path);

            if (line - 1 < 0 || line - 1 >= lines.Length) return null;
            var lineText = lines[line - 1].Trim();

            var match = Regex.Match(lineText, @"(?:class|struct|record|interface|enum)\s+(\w+)");
            if (match.Success) return match.Groups[1].Value;

            match = Regex.Match(lineText, @"(?:public|private|protected|internal|static|override|virtual|abstract|async)\s+.*?\s+(\w+)\s*[({]");
            if (match.Success) return match.Groups[1].Value;

            return null;
        }
        catch { return null; }
    }

    // -- Workspace edit application --

    protected async Task<bool> ApplyWorkspaceEditAsync(JToken result, CancellationToken ct)
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

    protected static int GetOffset(string[] lines, int line, int character)
    {
        var offset = 0;
        for (var i = 0; i < line && i < lines.Length; i++)
            offset += lines[i].Length + 1;
        return offset + character;
    }

    protected static string FormatWorkspaceEdit(JToken result, string description)
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
                var newText = edit["newText"]?.ToString() ?? "";
                var preview = newText.Length > 60 ? newText[..57] + "..." : newText;
                preview = preview.Replace("\r", "").Replace("\n", "\\n");
                sb.AppendLine($"    line {line}: {preview}");
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
            return $"{description}: no changes needed.";

        sb.Insert(0, $"{description}: {totalEdits} edit(s) across {fileCount} file(s):\n\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Apply TextEdit[] directly to a single file.</summary>
    protected async Task<int> ApplyTextEditsAsync(string filePath, JArray edits, CancellationToken ct)
    {
        if (edits.Count == 0) return 0;

        var text = await File.ReadAllTextAsync(filePath, ct);
        var lines = text.Split('\n');
        var count = 0;

        var editList = new List<(int sl, int sc, int el, int ec, string newText)>();
        foreach (var edit in edits)
        {
            var range = edit["range"];
            if (range is null) continue;
            editList.Add((
                range["start"]!["line"]!.Value<int>(),
                range["start"]!["character"]!.Value<int>(),
                range["end"]!["line"]!.Value<int>(),
                range["end"]!["character"]!.Value<int>(),
                edit["newText"]?.ToString() ?? ""));
            count++;
        }

        foreach (var (sl, sc, el, ec, newText) in editList.OrderByDescending(e => e.sl).ThenByDescending(e => e.sc))
        {
            var startOffset = GetOffset(lines, sl, sc);
            var endOffset = GetOffset(lines, el, ec);
            text = string.Concat(text.AsSpan(0, startOffset), newText, text.AsSpan(endOffset));
            lines = text.Split('\n');
        }

        await File.WriteAllTextAsync(filePath, text, ct);

        var uri = LspClient.PathToUri(filePath);
        await _lsp.NotifyAsync("textDocument/didClose", new JObject
        {
            ["textDocument"] = new JObject { ["uri"] = uri }
        });
        _lsp.MarkDocumentClosed(uri);

        return count;
    }

    // -- Path filter helpers --

    protected static List<(string pattern, bool isWildcard)>? ParsePathFilters(string? pathFilter)
    {
        if (pathFilter is null) return null;

        var filters = new List<(string pattern, bool isWildcard)>();
        foreach (var segment in pathFilter.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var isWildcard = segment.Contains('*') || segment.Contains('?');
            filters.Add((segment, isWildcard));
        }

        return filters.Count > 0 ? filters : null;
    }

    protected static bool MatchesAnyPathFilter(string path, List<(string pattern, bool isWildcard)> filters)
    {
        foreach (var (pattern, isWildcard) in filters)
        {
            if (isWildcard)
            {
                if (FileSystemName.MatchesSimpleExpression(pattern, path, ignoreCase: true))
                    return true;
            }
            else
            {
                if (path.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
