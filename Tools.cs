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

    [McpServerTool(Name = "load_solution"), Description("Load a solution or project file. Must be called before using any other tool. Accepts .sln, .slnx, or .csproj paths.")]
    public async Task<string> LoadSolution(
        [Description("Absolute path to the solution (.sln/.slnx) or project (.csproj) file")] string path,
        CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(path))
                return $"File not found: {path}";

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".sln" or ".slnx" or ".csproj"))
                return $"Unsupported file type '{ext}'. Expected .sln, .slnx, or .csproj";

            await _lsp.LoadSolutionAsync(path, ct);
            return $"Loaded {Path.GetFileName(path)}. Solution is loading in the background - first requests may be slow.";
        }
        catch (Exception ex) { return FormatError(ex); }
    }

    private string? RequireSolution()
    {
        if (!_lsp.IsSolutionLoaded)
            return "No solution loaded. Call load_solution first.";
        return null;
    }

    [McpServerTool(Name = "find_definition"), Description("Find the definition of a symbol by name in a file.")]
    public async Task<string> FindDefinition(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the symbol to find")] string symbolName,
        [Description("Kind of symbol: class, method, property, field, interface, enum, struct, etc.")] string? symbolKind = null,
        CancellationToken ct = default)
    {
        try
        {
            if (RequireSolution() is string err) return err;

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
            if (RequireSolution() is string err) return err;

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
            if (RequireSolution() is string err) return err;

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
        catch (Exception ex) { return FormatError(ex); }
    }

    [McpServerTool(Name = "find_document_symbols"), Description("List all symbols defined in a C# file.")]
    public async Task<string> FindDocumentSymbols(
        [Description("Absolute path to the C# file")] string filePath,
        CancellationToken ct = default)
    {
        try
        {
            if (RequireSolution() is string err) return err;

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
        catch (Exception ex) { return FormatError(ex); }
    }

    [McpServerTool(Name = "find_workspace_symbols"), Description("Search for symbols across the entire workspace. Optionally filter results to specific directories or project paths.")]
    public async Task<string> FindWorkspaceSymbols(
        [Description("Search query for the symbol name")] string query,
        [Description("Optional path filter (case-insensitive). Supports: substring match (\"Dune\"), wildcards (\"*\\\\Dune\\\\*.cs\"), and multiple filters separated by ; (\"Dune;CompanionApp\"). A symbol matches if ANY filter matches.")] string? pathFilter = null,
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
        catch (Exception ex) { return FormatError(ex); }
    }

    [McpServerTool(Name = "restart_lsp"), Description("Restart the roslyn-ls language server. Use when the server is in a bad state, returning stale results, or after solution changes.")]
    public async Task<string> RestartLsp(CancellationToken ct = default)
    {
        try
        {
            await _lsp.RestartAsync(ct);
            return "roslyn-ls restarted successfully. The solution will reload in the background - first requests may be slow.";
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
            if (RequireSolution() is string err) return err;

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
            if (RequireSolution() is string err) return err;

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
        catch (Exception ex) { return FormatError(ex); }
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
            if (RequireSolution() is string err) return err;

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
        catch (Exception ex) { return FormatError(ex); }
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
            if (RequireSolution() is string err) return err;

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
        catch (Exception ex) { return FormatError(ex); }
    }

    [McpServerTool(Name = "get_diagnostics"), Description("Get compiler errors and warnings for a C# file without building.")]
    public async Task<string> GetDiagnostics(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Minimum severity to include: error, warning, info, hint. Default: warning")] string? minSeverity = null,
        CancellationToken ct = default)
    {
        try
        {
            if (RequireSolution() is string err) return err;

            await _lsp.EnsureDocumentOpenAsync(filePath, ct);
            var uri = LspClient.PathToUri(filePath);

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
                var source = diag["source"]?.ToString();

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
            if (RequireSolution() is string err) return err;

            var pos = await _lsp.FindSymbolPositionAsync(filePath, symbolName, symbolKind, ct);
            if (pos is null)
                return $"Symbol '{symbolName}' not found in {filePath}";

            await _lsp.EnsureDocumentOpenAsync(filePath, ct);
            var uri = LspClient.PathToUri(filePath);

            // Check if rename is valid
            var prepareResult = await _lsp.RequestAsync("textDocument/prepareRename", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri },
                ["position"] = new JObject { ["line"] = pos.Value.line, ["character"] = pos.Value.character },
            }, ct);

            if (prepareResult is null)
                return $"Symbol '{symbolName}' cannot be renamed.";

            var result = await _lsp.RequestAsync("textDocument/rename", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri },
                ["position"] = new JObject { ["line"] = pos.Value.line, ["character"] = pos.Value.character },
                ["newName"] = newName,
            }, ct);

            if (result is null)
                return $"Rename failed - no changes returned.";

            var applied = await ApplyWorkspaceEditAsync(result, ct);
            var summary = FormatWorkspaceEdit(result, symbolName, newName);
            return applied ? summary : $"(dry-run, edits NOT applied to disk)\n\n{summary}";
        }
        catch (Exception ex) { return FormatError(ex); }
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
            if (RequireSolution() is string err) return err;

            var pos = await _lsp.FindSymbolPositionAsync(filePath, symbolName, symbolKind, ct);
            if (pos is null)
                return $"Symbol '{symbolName}' not found in {filePath}";

            await _lsp.EnsureDocumentOpenAsync(filePath, ct);
            var uri = LspClient.PathToUri(filePath);

            try
            {
                var prepareResult = await _lsp.RequestAsync("textDocument/prepareTypeHierarchy", new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = uri },
                    ["position"] = new JObject { ["line"] = pos.Value.line, ["character"] = pos.Value.character },
                }, ct);

                if (prepareResult is JArray items && items.Count > 0)
                {
                    var item = items[0];
                    var result = await _lsp.RequestAsync("typeHierarchy/supertypes", new JObject
                    {
                        ["item"] = item,
                    }, ct);

                    if (result is JArray types && types.Count > 0)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($"Supertypes of '{symbolName}':");
                        sb.AppendLine();
                        FormatTypeHierarchyItems(types, sb);
                        return sb.ToString().TrimEnd();
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* native LSP failed, use fallback */ }

            return await SupertypesFallback(filePath, symbolName, pos.Value.line, ct);
        }
        catch (Exception ex) { return FormatError(ex); }
    }

    private async Task<string> SupertypesFallback(string filePath, string symbolName, int line, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(filePath, ct);
        if (line >= lines.Length)
            return $"No supertypes found for '{symbolName}'.";

        var declLine = lines[line].Trim();

        // Parse "class Foo : Bar, IBaz" or "struct Foo : IBar"
        var colonIdx = declLine.IndexOf(':');
        if (colonIdx < 0)
            return $"No supertypes found for '{symbolName}' (no base type in declaration).";

        var afterColon = declLine[(colonIdx + 1)..].Trim();
        // Strip trailing { or where constraints
        var braceIdx = afterColon.IndexOf('{');
        if (braceIdx >= 0) afterColon = afterColon[..braceIdx];
        var whereIdx = afterColon.IndexOf(" where ", StringComparison.Ordinal);
        if (whereIdx >= 0) afterColon = afterColon[..whereIdx];

        var baseTypes = afterColon
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Split('<')[0].Trim()) // strip generic args for lookup
            .Where(t => t.Length > 0)
            .ToList();

        if (baseTypes.Count == 0)
            return $"No supertypes found for '{symbolName}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"Supertypes of '{symbolName}':");
        sb.AppendLine();

        foreach (var baseType in baseTypes)
        {
            var resolved = await ResolveTypeLocation(baseType, ct, filePath);

            if (resolved is not null)
                sb.AppendLine($"  {baseType} ({resolved.Value.kind}) - {resolved.Value.path}:{resolved.Value.line + 1}");
            else
                sb.AppendLine($"  {baseType} (external/unresolved)");
        }

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "subtypes"), Description("Find derived types and implementations of a type (type hierarchy - subtypes).")]
    public async Task<string> Subtypes(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the type")] string symbolName,
        [Description("Kind of symbol: class, interface, struct, enum")] string? symbolKind = null,
        [Description("Optional regex filter on type names in results")] string? filter = null,
        [Description("Max results to return. Default: 50")] int limit = 50,
        CancellationToken ct = default)
    {
        try
        {
            if (RequireSolution() is string err) return err;

            var pos = await _lsp.FindSymbolPositionAsync(filePath, symbolName, symbolKind, ct);
            if (pos is null)
                return $"Symbol '{symbolName}' not found in {filePath}";

            await _lsp.EnsureDocumentOpenAsync(filePath, ct);
            var uri = LspClient.PathToUri(filePath);

            try
            {
                var prepareResult = await _lsp.RequestAsync("textDocument/prepareTypeHierarchy", new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = uri },
                    ["position"] = new JObject { ["line"] = pos.Value.line, ["character"] = pos.Value.character },
                }, ct);

                if (prepareResult is JArray items && items.Count > 0)
                {
                    var item = items[0];
                    var result = await _lsp.RequestAsync("typeHierarchy/subtypes", new JObject
                    {
                        ["item"] = item,
                    }, ct);

                    if (result is JArray types && types.Count > 0)
                    {
                        var sb = new StringBuilder();
                        var filterRegex = filter is not null ? new System.Text.RegularExpressions.Regex(filter, System.Text.RegularExpressions.RegexOptions.IgnoreCase) : null;
                        var shown = 0;
                        var matched = 0;
                        var total = types.Count;

                        foreach (var t in types)
                        {
                            var name = t["name"]?.ToString() ?? "?";
                            if (filterRegex is not null && !filterRegex.IsMatch(name)) continue;
                            matched++;
                            if (shown >= limit) continue;
                            var kind = LspClient.SymbolKindName(t["kind"]?.Value<int>() ?? 0);
                            var tUri = t["uri"]?.ToString();
                            var path = tUri is not null ? LspClient.UriToPath(tUri) : "?";
                            var range = t["selectionRange"] ?? t["range"];
                            var line = (range?["start"]?["line"]?.Value<int>() ?? 0) + 1;
                            sb.AppendLine($"  {name} ({kind}) - {path}:{line}");
                            shown++;
                        }

                        if (shown > 0)
                        {
                            sb.Insert(0, FormatSubtypesHeader(symbolName, shown, matched, total, filter) + "\n\n");
                            return sb.ToString().TrimEnd();
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* native LSP failed, use fallback */ }

            return await SubtypesFallback(uri, symbolName, pos.Value, filter, limit, ct);
        }
        catch (Exception ex) { return FormatError(ex); }
    }

    private async Task<string> SubtypesFallback(string uri, string symbolName, (int line, int character) pos, string? filter, int limit, CancellationToken ct)
    {
        // Use textDocument/references to find where the type is referenced in inheritance declarations
        var refsResult = await _lsp.RequestAsync("textDocument/references", new JObject
        {
            ["textDocument"] = new JObject { ["uri"] = uri },
            ["position"] = new JObject { ["line"] = pos.line, ["character"] = pos.character },
            ["context"] = new JObject { ["includeDeclaration"] = false },
        }, ct);

        if (refsResult is not JArray refs || refs.Count == 0)
            return $"No subtypes found for '{symbolName}'.";

        var filterRegex = filter is not null ? new System.Text.RegularExpressions.Regex(filter, System.Text.RegularExpressions.RegexOptions.IgnoreCase) : null;
        var sb = new StringBuilder();
        var shown = 0;
        var matched = 0;
        var total = 0;
        var fileLineCache = new Dictionary<string, string[]>();

        foreach (var r in refs)
        {
            var refUri = r["uri"]?.ToString();
            var refLine = r["range"]?["start"]?["line"]?.Value<int>() ?? -1;
            if (refUri is null || refLine < 0) continue;

            var refPath = LspClient.UriToPath(refUri);
            if (!File.Exists(refPath)) continue;

            if (!fileLineCache.TryGetValue(refPath, out var lines))
                fileLineCache[refPath] = lines = await File.ReadAllLinesAsync(refPath, ct);

            if (refLine >= lines.Length) continue;

            var lineText = lines[refLine].Trim();
            // Must be a type declaration with inheritance
            var match = System.Text.RegularExpressions.Regex.Match(
                lineText, @"(?:class|struct|record|interface)\s+(\w+)");
            if (!match.Success) continue;

            var typeName = match.Groups[1].Value;
            if (typeName == symbolName) continue;

            // Verify the reference is in an inheritance context (after : or ,)
            if (!lineText.Contains($": {symbolName}", StringComparison.Ordinal) &&
                !lineText.Contains($", {symbolName}", StringComparison.Ordinal) &&
                !lineText.Contains($".{symbolName}", StringComparison.Ordinal))
                continue;

            total++;
            if (filterRegex is not null && !filterRegex.IsMatch(typeName)) continue;
            matched++;
            if (shown >= limit) continue;

            var kindWord = match.Value.Split(' ')[0].Trim();
            var kind = char.ToUpperInvariant(kindWord[0]) + kindWord[1..];
            sb.AppendLine($"  {typeName} ({kind}) - {refPath}:{refLine + 1}");
            shown++;
        }

        if (shown == 0)
            return $"No subtypes found for '{symbolName}'.";

        sb.Insert(0, FormatSubtypesHeader(symbolName, shown, matched, total, filter) + "\n\n");
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "type_hierarchy"), Description("Show full inheritance chain from a type up to its root base type.")]
    public async Task<string> TypeHierarchy(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the type")] string symbolName,
        [Description("Kind of symbol: class, interface, struct, enum")] string? symbolKind = null,
        CancellationToken ct = default)
    {
        try
        {
            if (RequireSolution() is string err) return err;

            var chain = new List<(string name, string kind, string path, int line)>();
            var currentFile = filePath;
            var currentName = symbolName;
            var visited = new HashSet<string>(StringComparer.Ordinal);

            // Find starting symbol position
            var pos = await _lsp.FindSymbolPositionAsync(currentFile, currentName, symbolKind, ct);
            if (pos is null)
                return $"Symbol '{symbolName}' not found in {filePath}";

            chain.Add((currentName, symbolKind ?? "Class", currentFile, pos.Value.line + 1));
            visited.Add(currentName);

            // Walk up the inheritance chain
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Read the declaration line to find base type
                var lines = await File.ReadAllLinesAsync(currentFile, ct);
                if (pos!.Value.line >= lines.Length) break;

                var declLine = lines[pos.Value.line].Trim();
                var colonIdx = declLine.IndexOf(':');
                if (colonIdx < 0) break;

                var afterColon = declLine[(colonIdx + 1)..].Trim();
                var braceIdx = afterColon.IndexOf('{');
                if (braceIdx >= 0) afterColon = afterColon[..braceIdx];
                var whereIdx = afterColon.IndexOf(" where ", StringComparison.Ordinal);
                if (whereIdx >= 0) afterColon = afterColon[..whereIdx];

                // Take just the first type (base class, not interfaces)
                var baseType = afterColon
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t =>
                    {
                        // Strip generic type args: Foo<T> -> Foo
                        var s = t.Split('<')[0].Trim();
                        // Strip primary constructor args: Foo(address) -> Foo
                        var parenIdx = s.IndexOf('(');
                        return parenIdx >= 0 ? s[..parenIdx].Trim() : s;
                    })
                    .FirstOrDefault(t => t.Length > 0);

                if (baseType is null || !visited.Add(baseType)) break;

                // Locate via workspace/symbol (handle qualified names like Script.CoreUObject.Object)
                var resolved = await ResolveTypeLocation(baseType, ct, currentFile);

                if (resolved is null)
                {
                    chain.Add((baseType, "external", "", 0));
                    break;
                }

                chain.Add((baseType, resolved.Value.kind, resolved.Value.path, resolved.Value.line + 1));
                currentFile = resolved.Value.path;
                pos = (resolved.Value.line, 0);
            }

            // Format as tree
            var sb = new StringBuilder();
            sb.AppendLine($"Type hierarchy for '{symbolName}':");
            sb.AppendLine();
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var (name, kind, path, line) = chain[i];
                var indent = new string(' ', (chain.Count - 1 - i) * 2);
                var arrow = i < chain.Count - 1 ? "-> " : "   ";
                if (path.Length > 0)
                    sb.AppendLine($"{indent}{arrow}{name} ({kind}) - {path}:{line}");
                else
                    sb.AppendLine($"{indent}{arrow}{name} ({kind})");
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return FormatError(ex); }
    }

    /// <summary>
    /// Resolves a type name to its source location via workspace/symbol.
    /// For qualified names (e.g., Script.CoreUObject.Object), searches by the full qualified name
    /// first (which pattern-matches in roslyn-ls), then falls back to the short name.
    /// Filters results to type symbols only and disambiguates using the source file path.
    /// </summary>
    private async Task<(string kind, string path, int line)?> ResolveTypeLocation(
        string typeName, CancellationToken ct, string? contextFilePath = null)
    {
        var isQualified = typeName.Contains('.');
        var shortName = isQualified ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;

        // For qualified names, search by the full name first - roslyn-ls does pattern matching
        // so "Script.CoreUObject.Object" will find symbols in CoreUObject files
        if (isQualified)
        {
            var qualifiedResult = await FindTypeSymbol(typeName, shortName, ct, contextFilePath);
            if (qualifiedResult is not null) return qualifiedResult;
        }

        // Fallback: search by short name
        return await FindTypeSymbol(shortName, shortName, ct, contextFilePath);
    }

    private async Task<(string kind, string path, int line)?> FindTypeSymbol(
        string query, string expectedName, CancellationToken ct, string? contextFilePath)
    {
        var result = await _lsp.RequestAsync("workspace/symbol", new JObject
        {
            ["query"] = query
        }, ct);

        if (result is not JArray symbols) return null;

        // Extract a project-identifying directory segment from context file path to prefer same-project matches.
        // E.g., from "F:\MonoRepo\extraction\palia-extraction\generated\Models\Script\Palia.cs"
        // we want "palia-extraction" so we match other files under the same extraction folder.
        // Use the longest matching segment to avoid picking "extraction" over "palia-extraction".
        string? contextSegment = null;
        if (contextFilePath is not null)
        {
            var parts = Path.GetDirectoryName(contextFilePath)?.Split(Path.DirectorySeparatorChar) ?? [];
            contextSegment = parts
                .Where(p => p.Contains("extraction", StringComparison.OrdinalIgnoreCase) ||
                            p.Contains("DatabaseBuilder", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Length)
                .FirstOrDefault();
        }

        (string kind, string path, int line)? sameProjectMatch = null;
        (string kind, string path, int line)? anyMatch = null;

        foreach (var sym in symbols)
        {
            var name = sym["name"]?.ToString();
            if (!string.Equals(name, expectedName, StringComparison.Ordinal))
                continue;

            // Only consider type symbols (class=5, enum=10, interface=11, struct=23)
            var symKind = sym["kind"]?.Value<int>() ?? 0;
            if (symKind is not (5 or 10 or 11 or 23)) continue;

            var kind = LspClient.SymbolKindName(symKind);
            var loc = sym["location"];
            var path = loc?["uri"] is JToken u ? LspClient.UriToPath(u.ToString()) : null;
            var symLine = loc?["range"]?["start"]?["line"]?.Value<int>() ?? 0;

            if (path is null) continue;

            // Prefer match from same project/extraction
            if (contextSegment is not null &&
                path.Contains(contextSegment, StringComparison.OrdinalIgnoreCase))
            {
                sameProjectMatch ??= (kind, path, symLine);
            }

            anyMatch ??= (kind, path, symLine);
        }

        return sameProjectMatch ?? anyMatch;
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

            // Apply edits in reverse order (bottom-up) to preserve positions
            foreach (var (sl, sc, el, ec, newText) in edits.OrderByDescending(e => e.startLine).ThenByDescending(e => e.startChar))
            {
                var startOffset = GetOffset(lines, sl, sc);
                var endOffset = GetOffset(lines, el, ec);
                text = string.Concat(text.AsSpan(0, startOffset), newText, text.AsSpan(endOffset));
                // Re-split after each edit since offsets shift
                lines = text.Split('\n');
            }

            await File.WriteAllTextAsync(path, text, ct);

            // Notify LSP that the file changed
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
            offset += lines[i].Length + 1; // +1 for \n
        return offset + character;
    }

    // -- Workspace edit formatting --

    private static string FormatWorkspaceEdit(JToken result, string oldName, string newName)
    {
        var sb = new StringBuilder();
        var totalEdits = 0;
        var fileCount = 0;

        // Handle documentChanges (TextDocumentEdit[])
        var docChanges = result["documentChanges"] as JArray;
        if (docChanges is not null)
        {
            foreach (var docChange in docChanges)
            {
                var docUri = docChange["textDocument"]?["uri"]?.ToString();
                if (docUri is null) continue;

                var path = LspClient.UriToPath(docUri);
                var edits = docChange["edits"] as JArray;
                if (edits is null || edits.Count == 0) continue;

                fileCount++;
                sb.AppendLine($"  {path} ({edits.Count} edit(s))");
                foreach (var edit in edits)
                {
                    var line = (edit["range"]?["start"]?["line"]?.Value<int>() ?? 0) + 1;
                    sb.AppendLine($"    line {line}: '{oldName}' -> '{newName}'");
                    totalEdits++;
                }
            }
        }

        // Handle changes { uri: TextEdit[] }
        var changes = result["changes"] as JObject;
        if (changes is not null)
        {
            foreach (var prop in changes.Properties())
            {
                var path = LspClient.UriToPath(prop.Name);
                var edits = prop.Value as JArray;
                if (edits is null || edits.Count == 0) continue;

                fileCount++;
                sb.AppendLine($"  {path} ({edits.Count} edit(s))");
                foreach (var edit in edits)
                {
                    var line = (edit["range"]?["start"]?["line"]?.Value<int>() ?? 0) + 1;
                    sb.AppendLine($"    line {line}: '{oldName}' -> '{newName}'");
                    totalEdits++;
                }
            }
        }

        if (totalEdits == 0)
            return $"Rename '{oldName}' -> '{newName}': no changes needed.";

        sb.Insert(0, $"Rename '{oldName}' -> '{newName}': {totalEdits} edit(s) across {fileCount} file(s):\n\n");
        return sb.ToString().TrimEnd();
    }

    // -- Error formatting --

    private static string FormatSubtypesHeader(string symbolName, int shown, int matched, int total, string? filter)
    {
        // "Subtypes of 'X' (showing 10 of 45 matching '^BP_', 622 total):"
        var sb = new StringBuilder($"Subtypes of '{symbolName}' (");
        if (filter is not null)
        {
            if (shown < matched)
                sb.Append($"showing {shown} of {matched} matching '{filter}'");
            else
                sb.Append($"{matched} matching '{filter}'");
            sb.Append($", {total} total");
        }
        else
        {
            if (shown < total)
                sb.Append($"showing {shown} of {total}");
            else
                sb.Append($"{total}");
        }
        sb.Append("):");
        return sb.ToString();
    }

    private static string FormatError(Exception ex)
    {
        if (ex is OperationCanceledException)
            return "Request timed out. The language server may still be loading the solution. Try again in a moment.";

        var sb = new StringBuilder();
        sb.AppendLine($"Error: {ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException is not null)
            sb.AppendLine($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        return sb.ToString().TrimEnd();
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

    private static string FormatLocations(JToken? result, string label, string? filter = null, int limit = 0)
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

        var filterRegex = filter is not null ? new System.Text.RegularExpressions.Regex(filter, System.Text.RegularExpressions.RegexOptions.IgnoreCase) : null;
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

        var header = new StringBuilder($"Found ");
        if (filter is not null)
        {
            if (shown < matched)
                header.Append($"{shown} of {matched} {label}(s) matching '{filter}'");
            else
                header.Append($"{matched} {label}(s) matching '{filter}'");
            header.Append($" ({total} total)");
        }
        else
        {
            if (shown < total)
                header.Append($"{shown} of {total} {label}(s)");
            else
                header.Append($"{total} {label}(s)");
        }
        sb.Insert(0, header + ":\n\n");

        return sb.ToString().TrimEnd();
    }

    private static string? ExtractSymbolNameFromFile(string path, int line, Dictionary<string, string[]> cache)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (!cache.TryGetValue(path, out var lines))
                cache[path] = lines = File.ReadAllLines(path);

            if (line - 1 < 0 || line - 1 >= lines.Length) return null;
            var lineText = lines[line - 1].Trim();

            // Try to extract a type or member name from the declaration
            var match = System.Text.RegularExpressions.Regex.Match(
                lineText, @"(?:class|struct|record|interface|enum)\s+(\w+)");
            if (match.Success) return match.Groups[1].Value;

            // Try method/property pattern
            match = System.Text.RegularExpressions.Regex.Match(
                lineText, @"(?:public|private|protected|internal|static|override|virtual|abstract|async)\s+.*?\s+(\w+)\s*[({]");
            if (match.Success) return match.Groups[1].Value;

            return null;
        }
        catch { return null; }
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
