using System.ComponentModel;
using System.IO.Enumeration;
using System.Text;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace SharpLS.MCP;

[McpServerToolType]
public class LspTools
{
    private readonly LspClient _lsp;

    public LspTools(LspClient lsp) => _lsp = lsp;

    [McpServerTool(Name = "find_definition"), Description("Find the definition of a symbol by name in a file.")]
    public async Task<string> FindDefinition(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the symbol to find")] string symbolName,
        [Description("Kind of symbol: class, method, property, field, interface, enum, struct, etc.")] string? symbolKind = null,
        CancellationToken ct = default)
    {
        try
        {
            var pos = await _lsp.FindSymbolPositionAsync(filePath, symbolName, symbolKind, ct);
            if (pos is null)
                return $"Symbol '{symbolName}' not found in {filePath}";

            await _lsp.EnsureDocumentOpenAsync(filePath, ct);
            var uri = LspClient.PathToUri(filePath);

            var result = await _lsp.RequestAsync("textDocument/definition", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri },
                ["position"] = new JObject { ["line"] = pos.Value.line, ["character"] = pos.Value.character },
            }, ct);

            return FormatLocations(result, "definition");
        }
        catch (OperationCanceledException)
        {
            return "Request timed out. The language server may still be loading the solution. Try again in a moment.";
        }
    }

    [McpServerTool(Name = "find_references"), Description("Find all references to a symbol across the workspace.")]
    public async Task<string> FindReferences(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the symbol to find")] string symbolName,
        [Description("Kind of symbol: class, method, property, field, interface, enum, struct, etc.")] string? symbolKind = null,
        [Description("Include the declaration itself")] bool includeDeclaration = true,
        CancellationToken ct = default)
    {
        try
        {
            var pos = await _lsp.FindSymbolPositionAsync(filePath, symbolName, symbolKind, ct);
            if (pos is null)
                return $"Symbol '{symbolName}' not found in {filePath}";

            await _lsp.EnsureDocumentOpenAsync(filePath, ct);
            var uri = LspClient.PathToUri(filePath);

            var result = await _lsp.RequestAsync("textDocument/references", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri },
                ["position"] = new JObject { ["line"] = pos.Value.line, ["character"] = pos.Value.character },
                ["context"] = new JObject { ["includeDeclaration"] = includeDeclaration },
            }, ct);

            return FormatLocations(result, "reference");
        }
        catch (OperationCanceledException)
        {
            return "Request timed out. The language server may still be loading the solution. Try again in a moment.";
        }
    }

    [McpServerTool(Name = "get_hover"), Description("Get hover documentation and type info for a symbol.")]
    public async Task<string> GetHover(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the symbol to find")] string symbolName,
        [Description("Kind of symbol: class, method, property, field, interface, enum, struct, etc.")] string? symbolKind = null,
        CancellationToken ct = default)
    {
        try
        {
            var pos = await _lsp.FindSymbolPositionAsync(filePath, symbolName, symbolKind, ct);
            if (pos is null)
                return $"Symbol '{symbolName}' not found in {filePath}";

            await _lsp.EnsureDocumentOpenAsync(filePath, ct);
            var uri = LspClient.PathToUri(filePath);

            var result = await _lsp.RequestAsync("textDocument/hover", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri },
                ["position"] = new JObject { ["line"] = pos.Value.line, ["character"] = pos.Value.character },
            }, ct);

            if (result is null) return "No hover information available.";

            var contents = result["contents"];
            if (contents is null) return "No hover information available.";

            // Handle MarkupContent
            if (contents["value"] is JToken val)
                return val.ToString();

            // Handle MarkedString or array
            if (contents is JArray arr)
            {
                var sb = new StringBuilder();
                foreach (var item in arr)
                {
                    if (item is JValue v)
                        sb.AppendLine(v.ToString());
                    else if (item["value"] is JToken itemVal)
                        sb.AppendLine(itemVal.ToString());
                }
                return sb.ToString().TrimEnd();
            }

            return contents.ToString();
        }
        catch (OperationCanceledException)
        {
            return "Request timed out. The language server may still be loading the solution. Try again in a moment.";
        }
    }

    [McpServerTool(Name = "find_document_symbols"), Description("List all symbols defined in a C# file.")]
    public async Task<string> FindDocumentSymbols(
        [Description("Absolute path to the C# file")] string filePath,
        CancellationToken ct = default)
    {
        try
        {
            await _lsp.EnsureDocumentOpenAsync(filePath, ct);
            var uri = LspClient.PathToUri(filePath);

            var result = await _lsp.RequestAsync("textDocument/documentSymbol", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri },
            }, ct);

            if (result is not JArray symbols || symbols.Count == 0)
                return "No symbols found.";

            var sb = new StringBuilder();
            FormatSymbolTree(symbols, sb, indent: 0);
            return sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException)
        {
            return "Request timed out. The language server may still be loading the solution. Try again in a moment.";
        }
    }

    [McpServerTool(Name = "find_workspace_symbols"), Description("Search for symbols across the entire workspace. Optionally filter results to specific directories or project paths.")]
    public async Task<string> FindWorkspaceSymbols(
        [Description("Search query for the symbol name")] string query,
        [Description("Optional path filter (case-insensitive). Supports: substring match (\"Dune\"), wildcards (\"*\\\\Dune\\\\*.cs\"), and multiple filters separated by ; (\"Dune;CompanionApp\"). A symbol matches if ANY filter matches.")] string? pathFilter = null,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _lsp.RequestAsync("workspace/symbol", new JObject
            {
                ["query"] = query,
            }, ct);

            if (result is not JArray symbols || symbols.Count == 0)
                return $"No symbols matching '{query}' found.";

            var filters = ParsePathFilters(pathFilter);
            var sb = new StringBuilder();
            var count = 0;

            foreach (var sym in symbols)
            {
                var name = sym["name"]?.ToString() ?? "?";
                var kind = LspClient.SymbolKindName(sym["kind"]?.Value<int>() ?? 0);
                var loc = sym["location"];
                var path = loc?["uri"] is JToken u ? LspClient.UriToPath(u.ToString()) : "?";
                var line = (loc?["range"]?["start"]?["line"]?.Value<int>() ?? 0) + 1;

                if (filters is not null && !MatchesAnyPathFilter(path, filters))
                    continue;

                sb.AppendLine($"  {name} ({kind}) - {path}:{line}");
                count++;
            }

            if (count == 0)
                return pathFilter is not null
                    ? $"No symbols matching '{query}' found in paths matching '{pathFilter}'."
                    : $"No symbols matching '{query}' found.";

            sb.Insert(0, $"Found {count} symbol(s) matching '{query}'" +
                (pathFilter is not null ? $" (filtered to '{pathFilter}')" : "") +
                ":\n\n");

            return sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException)
        {
            return "Request timed out. The language server may still be loading the solution. Try again in a moment.";
        }
    }

    [McpServerTool(Name = "restart_lsp"), Description("Restart the csharp-ls language server. Use when the server is in a bad state, returning stale results, or after solution changes.")]
    public async Task<string> RestartLsp(CancellationToken ct = default)
    {
        try
        {
            await _lsp.RestartAsync(ct);
            return "csharp-ls restarted successfully. The solution will reload in the background - first requests may be slow.";
        }
        catch (Exception ex)
        {
            return $"Failed to restart csharp-ls: {ex.Message}";
        }
    }

    [McpServerTool(Name = "go_to_implementation"), Description("Find implementations of an interface or abstract method.")]
    public async Task<string> GoToImplementation(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the symbol to find")] string symbolName,
        [Description("Kind of symbol: class, method, property, field, interface, enum, struct, etc.")] string? symbolKind = null,
        CancellationToken ct = default)
    {
        try
        {
            var pos = await _lsp.FindSymbolPositionAsync(filePath, symbolName, symbolKind, ct);
            if (pos is null)
                return $"Symbol '{symbolName}' not found in {filePath}";

            await _lsp.EnsureDocumentOpenAsync(filePath, ct);
            var uri = LspClient.PathToUri(filePath);

            var result = await _lsp.RequestAsync("textDocument/implementation", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri },
                ["position"] = new JObject { ["line"] = pos.Value.line, ["character"] = pos.Value.character },
            }, ct);

            return FormatLocations(result, "implementation");
        }
        catch (OperationCanceledException)
        {
            return "Request timed out. The language server may still be loading the solution. Try again in a moment.";
        }
    }

    [McpServerTool(Name = "go_to_type_definition"), Description("Jump to the type definition of a symbol (e.g. find the class of a variable).")]
    public async Task<string> GoToTypeDefinition(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the symbol to find")] string symbolName,
        [Description("Kind of symbol: class, method, property, field, interface, enum, struct, etc.")] string? symbolKind = null,
        CancellationToken ct = default)
    {
        try
        {
            var pos = await _lsp.FindSymbolPositionAsync(filePath, symbolName, symbolKind, ct);
            if (pos is null)
                return $"Symbol '{symbolName}' not found in {filePath}";

            await _lsp.EnsureDocumentOpenAsync(filePath, ct);
            var uri = LspClient.PathToUri(filePath);

            var result = await _lsp.RequestAsync("textDocument/typeDefinition", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri },
                ["position"] = new JObject { ["line"] = pos.Value.line, ["character"] = pos.Value.character },
            }, ct);

            return FormatLocations(result, "type definition");
        }
        catch (OperationCanceledException)
        {
            return "Request timed out. The language server may still be loading the solution. Try again in a moment.";
        }
    }

    [McpServerTool(Name = "incoming_calls"), Description("Find all functions/methods that call the specified symbol.")]
    public async Task<string> IncomingCalls(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the symbol to find")] string symbolName,
        [Description("Kind of symbol: class, method, property, field, interface, enum, struct, etc.")] string? symbolKind = null,
        CancellationToken ct = default)
    {
        try
        {
            var pos = await _lsp.FindSymbolPositionAsync(filePath, symbolName, symbolKind, ct);
            if (pos is null)
                return $"Symbol '{symbolName}' not found in {filePath}";

            await _lsp.EnsureDocumentOpenAsync(filePath, ct);
            var uri = LspClient.PathToUri(filePath);

            var prepareResult = await _lsp.RequestAsync("textDocument/prepareCallHierarchy", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri },
                ["position"] = new JObject { ["line"] = pos.Value.line, ["character"] = pos.Value.character },
            }, ct);

            if (prepareResult is not JArray items || items.Count == 0)
                return $"No call hierarchy item found for '{symbolName}'.";

            var item = items[0];
            var result = await _lsp.RequestAsync("callHierarchy/incomingCalls", new JObject
            {
                ["item"] = item,
            }, ct);

            if (result is not JArray calls || calls.Count == 0)
                return $"No incoming calls found for '{symbolName}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"Found {calls.Count} caller(s) of '{symbolName}':");
            sb.AppendLine();

            foreach (var call in calls)
            {
                var from = call["from"];
                if (from is null) continue;

                var name = from["name"]?.ToString() ?? "?";
                var kind = LspClient.SymbolKindName(from["kind"]?.Value<int>() ?? 0);
                var fromUri = from["uri"]?.ToString();
                var path = fromUri is not null ? LspClient.UriToPath(fromUri) : "?";
                var range = from["selectionRange"] ?? from["range"];
                var line = (range?["start"]?["line"]?.Value<int>() ?? 0) + 1;

                sb.AppendLine($"  {name} ({kind}) - {path}:{line}");
            }

            return sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException)
        {
            return "Request timed out. The language server may still be loading the solution. Try again in a moment.";
        }
    }

    [McpServerTool(Name = "outgoing_calls"), Description("Find all functions/methods called by the specified symbol.")]
    public async Task<string> OutgoingCalls(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the symbol to find")] string symbolName,
        [Description("Kind of symbol: class, method, property, field, interface, enum, struct, etc.")] string? symbolKind = null,
        CancellationToken ct = default)
    {
        try
        {
            var pos = await _lsp.FindSymbolPositionAsync(filePath, symbolName, symbolKind, ct);
            if (pos is null)
                return $"Symbol '{symbolName}' not found in {filePath}";

            await _lsp.EnsureDocumentOpenAsync(filePath, ct);
            var uri = LspClient.PathToUri(filePath);

            var prepareResult = await _lsp.RequestAsync("textDocument/prepareCallHierarchy", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri },
                ["position"] = new JObject { ["line"] = pos.Value.line, ["character"] = pos.Value.character },
            }, ct);

            if (prepareResult is not JArray items || items.Count == 0)
                return $"No call hierarchy item found for '{symbolName}'.";

            var item = items[0];
            var result = await _lsp.RequestAsync("callHierarchy/outgoingCalls", new JObject
            {
                ["item"] = item,
            }, ct);

            if (result is not JArray calls || calls.Count == 0)
                return $"No outgoing calls found from '{symbolName}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"Found {calls.Count} call(s) from '{symbolName}':");
            sb.AppendLine();

            foreach (var call in calls)
            {
                var to = call["to"];
                if (to is null) continue;

                var name = to["name"]?.ToString() ?? "?";
                var kind = LspClient.SymbolKindName(to["kind"]?.Value<int>() ?? 0);
                var toUri = to["uri"]?.ToString();
                var path = toUri is not null ? LspClient.UriToPath(toUri) : "?";
                var range = to["selectionRange"] ?? to["range"];
                var line = (range?["start"]?["line"]?.Value<int>() ?? 0) + 1;

                sb.AppendLine($"  {name} ({kind}) - {path}:{line}");
            }

            return sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException)
        {
            return "Request timed out. The language server may still be loading the solution. Try again in a moment.";
        }
    }

    [McpServerTool(Name = "supertypes"), Description("Find base types and interfaces of a type (type hierarchy - supertypes).")]
    public async Task<string> Supertypes(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the type")] string symbolName,
        [Description("Kind of symbol: class, interface, struct, enum")] string? symbolKind = null,
        CancellationToken ct = default)
    {
        try
        {
            var pos = await _lsp.FindSymbolPositionAsync(filePath, symbolName, symbolKind, ct);
            if (pos is null)
                return $"Symbol '{symbolName}' not found in {filePath}";

            await _lsp.EnsureDocumentOpenAsync(filePath, ct);
            var uri = LspClient.PathToUri(filePath);

            var prepareResult = await _lsp.RequestAsync("textDocument/prepareTypeHierarchy", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri },
                ["position"] = new JObject { ["line"] = pos.Value.line, ["character"] = pos.Value.character },
            }, ct);

            if (prepareResult is not JArray items || items.Count == 0)
                return $"No type hierarchy item found for '{symbolName}'.";

            var item = items[0];
            var result = await _lsp.RequestAsync("typeHierarchy/supertypes", new JObject
            {
                ["item"] = item,
            }, ct);

            if (result is not JArray types || types.Count == 0)
                return $"No supertypes found for '{symbolName}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"Supertypes of '{symbolName}':");
            sb.AppendLine();
            FormatTypeHierarchyItems(types, sb);
            return sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException)
        {
            return "Request timed out. The language server may still be loading the solution. Try again in a moment.";
        }
    }

    [McpServerTool(Name = "subtypes"), Description("Find derived types and implementations of a type (type hierarchy - subtypes).")]
    public async Task<string> Subtypes(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the type")] string symbolName,
        [Description("Kind of symbol: class, interface, struct, enum")] string? symbolKind = null,
        CancellationToken ct = default)
    {
        try
        {
            var pos = await _lsp.FindSymbolPositionAsync(filePath, symbolName, symbolKind, ct);
            if (pos is null)
                return $"Symbol '{symbolName}' not found in {filePath}";

            await _lsp.EnsureDocumentOpenAsync(filePath, ct);
            var uri = LspClient.PathToUri(filePath);

            var prepareResult = await _lsp.RequestAsync("textDocument/prepareTypeHierarchy", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri },
                ["position"] = new JObject { ["line"] = pos.Value.line, ["character"] = pos.Value.character },
            }, ct);

            if (prepareResult is not JArray items || items.Count == 0)
                return $"No type hierarchy item found for '{symbolName}'.";

            var item = items[0];
            var result = await _lsp.RequestAsync("typeHierarchy/subtypes", new JObject
            {
                ["item"] = item,
            }, ct);

            if (result is not JArray types || types.Count == 0)
                return $"No subtypes found for '{symbolName}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"Subtypes of '{symbolName}':");
            sb.AppendLine();
            FormatTypeHierarchyItems(types, sb);
            return sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException)
        {
            return "Request timed out. The language server may still be loading the solution. Try again in a moment.";
        }
    }

    // -- Path filter helpers --

    private static List<(string pattern, bool isWildcard)>? ParsePathFilters(string? pathFilter)
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

    private static bool MatchesAnyPathFilter(string path, List<(string pattern, bool isWildcard)> filters)
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

    // -- Formatting helpers --

    private static string FormatLocations(JToken? result, string label)
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

        var sb = new StringBuilder();
        sb.AppendLine($"Found {locations.Count} {label}(s):");
        foreach (var (path, line, col) in locations)
            sb.AppendLine($"  {path}:{line}:{col}");

        return sb.ToString().TrimEnd();
    }

    private static void ExtractLocation(JToken item, List<(string path, int line, int col)> locations)
    {
        // Handle Location { uri, range }
        if (item["uri"] is JToken uri && item["range"] is JToken range)
        {
            var path = LspClient.UriToPath(uri.ToString());
            var line = (range["start"]?["line"]?.Value<int>() ?? 0) + 1;
            var col = (range["start"]?["character"]?.Value<int>() ?? 0) + 1;
            locations.Add((path, line, col));
        }
        // Handle LocationLink { targetUri, targetRange }
        else if (item["targetUri"] is JToken targetUri && item["targetRange"] is JToken targetRange)
        {
            var path = LspClient.UriToPath(targetUri.ToString());
            var line = (targetRange["start"]?["line"]?.Value<int>() ?? 0) + 1;
            var col = (targetRange["start"]?["character"]?.Value<int>() ?? 0) + 1;
            locations.Add((path, line, col));
        }
    }

    private static void FormatSymbolTree(JArray symbols, StringBuilder sb, int indent)
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

    private static void FormatTypeHierarchyItems(JArray items, StringBuilder sb)
    {
        foreach (var item in items)
        {
            var name = item["name"]?.ToString() ?? "?";
            var kind = LspClient.SymbolKindName(item["kind"]?.Value<int>() ?? 0);
            var itemUri = item["uri"]?.ToString();
            var path = itemUri is not null ? LspClient.UriToPath(itemUri) : "?";
            var range = item["selectionRange"] ?? item["range"];
            var line = (range?["start"]?["line"]?.Value<int>() ?? 0) + 1;

            sb.AppendLine($"  {name} ({kind}) - {path}:{line}");
        }
    }
}
