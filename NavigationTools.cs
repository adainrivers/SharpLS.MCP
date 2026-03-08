using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace SharpLS.MCP;

[McpServerToolType]
public class NavigationTools : ToolBase
{
    public NavigationTools(LspClient lsp) : base(lsp) { }

    [McpServerTool(Name = "find_definition"), Description("Find the definition of a symbol by name in a file.")]
    public async Task<string> FindDefinition(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the symbol to find")] string symbolName,
        [Description("Kind of symbol: class, method, property, field, interface, enum, struct, etc.")] string? symbolKind = null,
        CancellationToken ct = default)
    {
        try
        {
            var (ctx, err) = await PrepareSymbolRequest(filePath, symbolName, symbolKind, ct);
            if (ctx is null) return err!;

            var result = await _lsp.RequestAsync("textDocument/definition", MakePositionParams(ctx), ct);
            return FormatLocations(result, "definition");
        }
        catch (Exception ex) { return FormatError(ex); }
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
            var (ctx, err) = await PrepareSymbolRequest(filePath, symbolName, symbolKind, ct);
            if (ctx is null) return err!;

            var @params = MakePositionParams(ctx);
            @params["context"] = new JObject { ["includeDeclaration"] = includeDeclaration };

            var result = await _lsp.RequestAsync("textDocument/references", @params, ct);
            return FormatLocations(result, "reference");
        }
        catch (Exception ex) { return FormatError(ex); }
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
            var (ctx, err) = await PrepareSymbolRequest(filePath, symbolName, symbolKind, ct);
            if (ctx is null) return err!;

            var result = await _lsp.RequestAsync("textDocument/hover", MakePositionParams(ctx), ct);

            if (result is null) return "No hover information available.";

            var contents = result["contents"];
            if (contents is null) return "No hover information available.";

            if (contents["value"] is JToken val)
                return val.ToString();

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
        catch (Exception ex) { return FormatError(ex); }
    }

    [McpServerTool(Name = "go_to_implementation"), Description("Find implementations of an interface or abstract method.")]
    public async Task<string> GoToImplementation(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the symbol to find")] string symbolName,
        [Description("Kind of symbol: class, method, property, field, interface, enum, struct, etc.")] string? symbolKind = null,
        [Description("Optional regex filter on symbol/file names in results")] string? filter = null,
        [Description("Max results to return. Default: 50")] int limit = 50,
        CancellationToken ct = default)
    {
        try
        {
            var (ctx, err) = await PrepareSymbolRequest(filePath, symbolName, symbolKind, ct);
            if (ctx is null) return err!;

            var result = await _lsp.RequestAsync("textDocument/implementation", MakePositionParams(ctx), ct);
            return FormatLocations(result, "implementation", filter, limit);
        }
        catch (Exception ex) { return FormatError(ex); }
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
            var (ctx, err) = await PrepareSymbolRequest(filePath, symbolName, symbolKind, ct);
            if (ctx is null) return err!;

            var result = await _lsp.RequestAsync("textDocument/typeDefinition", MakePositionParams(ctx), ct);
            return FormatLocations(result, "type definition");
        }
        catch (Exception ex) { return FormatError(ex); }
    }

    [McpServerTool(Name = "find_document_symbols"), Description("List all symbols defined in a C# file.")]
    public async Task<string> FindDocumentSymbols(
        [Description("Absolute path to the C# file")] string filePath,
        CancellationToken ct = default)
    {
        try
        {
            var (uri, err) = await PrepareDocumentRequest(filePath, ct);
            if (uri is null) return err!;

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
        catch (Exception ex) { return FormatError(ex); }
    }

    [McpServerTool(Name = "find_workspace_symbols"), Description("Search for symbols across the entire workspace. Optionally filter results to specific directories or project paths.")]
    public async Task<string> FindWorkspaceSymbols(
        [Description("Search query for the symbol name")] string query,
        [Description("Optional path filter (case-insensitive). Supports: substring match (\"Dune\"), wildcards (\"*\\\\Dune\\\\*.cs\"), and multiple filters separated by ; (\"Dune;CompanionApp\"). A symbol matches if ANY filter matches.")] string? pathFilter = null,
        [Description("Optional regex filter on symbol names (e.g., '^Actor$' for exact match)")] string? filter = null,
        [Description("Optional regex filter on container/namespace (e.g., 'Script\\.Engine')")] string? namespaceFilter = null,
        [Description("Max results to return. Default: 50")] int limit = 50,
        CancellationToken ct = default)
    {
        try
        {
            if (RequireSolution() is string err) return err;

            var result = await _lsp.RequestAsync("workspace/symbol", new JObject
            {
                ["query"] = query,
            }, ct);

            if (result is not JArray symbols || symbols.Count == 0)
                return $"No symbols matching '{query}' found.";

            var filters = ParsePathFilters(pathFilter);
            var filterRegex = filter is not null ? new Regex(filter, RegexOptions.IgnoreCase) : null;
            var nsRegex = namespaceFilter is not null ? new Regex(namespaceFilter, RegexOptions.IgnoreCase) : null;
            var sb = new StringBuilder();
            var shown = 0;
            var matched = 0;
            var total = 0;

            foreach (var sym in symbols)
            {
                var name = sym["name"]?.ToString() ?? "?";
                var kind = LspClient.SymbolKindName(sym["kind"]?.Value<int>() ?? 0);
                var container = sym["containerName"]?.ToString() ?? "";
                var loc = sym["location"];
                var path = loc?["uri"] is JToken u ? LspClient.UriToPath(u.ToString()) : "?";
                var line = (loc?["range"]?["start"]?["line"]?.Value<int>() ?? 0) + 1;

                if (filters is not null && !MatchesAnyPathFilter(path, filters))
                    continue;

                total++;
                if (filterRegex is not null && !filterRegex.IsMatch(name)) continue;
                if (nsRegex is not null && !nsRegex.IsMatch(container)) continue;
                matched++;
                if (shown >= limit) continue;

                var containerSuffix = container.Length > 0 ? $" [{container}]" : "";
                sb.AppendLine($"  {name} ({kind}){containerSuffix} - {path}:{line}");
                shown++;
            }

            if (shown == 0)
                return $"No symbols matching '{query}' found" +
                    (filter is not null ? $" with filter '{filter}'" : "") +
                    (pathFilter is not null ? $" in paths matching '{pathFilter}'" : "") + ".";

            var header = new StringBuilder($"Found ");
            if (filter is not null)
            {
                if (shown < matched)
                    header.Append($"{shown} of {matched} symbol(s) matching '{filter}'");
                else
                    header.Append($"{matched} symbol(s) matching '{filter}'");
                header.Append($" ({total} total for '{query}')");
            }
            else
            {
                if (shown < total)
                    header.Append($"{shown} of {total} symbol(s) matching '{query}'");
                else
                    header.Append($"{total} symbol(s) matching '{query}'");
            }
            if (pathFilter is not null) header.Append($" (path: '{pathFilter}')");
            sb.Insert(0, header + ":\n\n");

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return FormatError(ex); }
    }
}
