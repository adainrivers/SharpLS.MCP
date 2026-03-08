using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace SharpLS.MCP;

[McpServerToolType]
public class HierarchyTools : ToolBase
{
    public HierarchyTools(LspClient lsp) : base(lsp) { }

    [McpServerTool(Name = "incoming_calls"), Description("Find all functions/methods that call the specified symbol.")]
    public async Task<string> IncomingCalls(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the symbol to find")] string symbolName,
        [Description("Kind of symbol: class, method, property, field, interface, enum, struct, etc.")] string? symbolKind = null,
        CancellationToken ct = default)
    {
        try
        {
            var (ctx, err) = await PrepareSymbolRequest(filePath, symbolName, symbolKind, ct);
            if (ctx is null) return err!;

            // Try native call hierarchy first
            try
            {
                var prepareResult = await _lsp.RequestAsync("textDocument/prepareCallHierarchy", MakePositionParams(ctx), ct);
                if (prepareResult is JArray items && items.Count > 0)
                {
                    var result = await _lsp.RequestAsync("callHierarchy/incomingCalls", new JObject { ["item"] = items[0] }, ct);
                    if (result is JArray calls && calls.Count > 0)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($"Found {calls.Count} caller(s) of '{symbolName}':");
                        sb.AppendLine();
                        foreach (var call in calls)
                        {
                            if (call["from"] is JToken from)
                                sb.AppendLine(FormatHierarchyItem(from));
                        }
                        return sb.ToString().TrimEnd();
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* native call hierarchy not supported, use fallback */ }

            return await IncomingCallsFallback(ctx, symbolName, ct);
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
            var (ctx, err) = await PrepareSymbolRequest(filePath, symbolName, symbolKind, ct);
            if (ctx is null) return err!;

            // Try native call hierarchy first
            try
            {
                var prepareResult = await _lsp.RequestAsync("textDocument/prepareCallHierarchy", MakePositionParams(ctx), ct);
                if (prepareResult is JArray items && items.Count > 0)
                {
                    var result = await _lsp.RequestAsync("callHierarchy/outgoingCalls", new JObject { ["item"] = items[0] }, ct);
                    if (result is JArray calls && calls.Count > 0)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($"Found {calls.Count} call(s) from '{symbolName}':");
                        sb.AppendLine();
                        foreach (var call in calls)
                        {
                            if (call["to"] is JToken to)
                                sb.AppendLine(FormatHierarchyItem(to));
                        }
                        return sb.ToString().TrimEnd();
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* native call hierarchy not supported, use fallback */ }

            return await OutgoingCallsFallback(ctx, filePath, symbolName, ct);
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
            var (ctx, err) = await PrepareSymbolRequest(filePath, symbolName, symbolKind, ct);
            if (ctx is null) return err!;

            try
            {
                var prepareResult = await _lsp.RequestAsync("textDocument/prepareTypeHierarchy", MakePositionParams(ctx), ct);
                if (prepareResult is JArray items && items.Count > 0)
                {
                    var result = await _lsp.RequestAsync("typeHierarchy/supertypes", new JObject { ["item"] = items[0] }, ct);
                    if (result is JArray types && types.Count > 0)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($"Supertypes of '{symbolName}':");
                        sb.AppendLine();
                        foreach (var t in types)
                            sb.AppendLine(FormatHierarchyItem(t));
                        return sb.ToString().TrimEnd();
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* native LSP failed, use fallback */ }

            return await SupertypesFallback(filePath, symbolName, ctx.Line, ct);
        }
        catch (Exception ex) { return FormatError(ex); }
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
            var (ctx, err) = await PrepareSymbolRequest(filePath, symbolName, symbolKind, ct);
            if (ctx is null) return err!;

            try
            {
                var prepareResult = await _lsp.RequestAsync("textDocument/prepareTypeHierarchy", MakePositionParams(ctx), ct);
                if (prepareResult is JArray items && items.Count > 0)
                {
                    var result = await _lsp.RequestAsync("typeHierarchy/subtypes", new JObject { ["item"] = items[0] }, ct);
                    if (result is JArray types && types.Count > 0)
                    {
                        var filterRegex = filter is not null ? new Regex(filter, RegexOptions.IgnoreCase) : null;
                        var sb = new StringBuilder();
                        var shown = 0;
                        var matched = 0;

                        foreach (var t in types)
                        {
                            var name = t["name"]?.ToString() ?? "?";
                            if (filterRegex is not null && !filterRegex.IsMatch(name)) continue;
                            matched++;
                            if (shown >= limit) continue;
                            sb.AppendLine(FormatHierarchyItem(t));
                            shown++;
                        }

                        if (shown > 0)
                        {
                            sb.Insert(0, FormatFilteredHeader($"Subtypes of '{symbolName}' (", shown, matched, types.Count, filter) + "\n\n");
                            return sb.ToString().TrimEnd();
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* native LSP failed, use fallback */ }

            return await SubtypesFallback(ctx.Uri, symbolName, (ctx.Line, ctx.Character), filter, limit, ct);
        }
        catch (Exception ex) { return FormatError(ex); }
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

            var pos = await _lsp.FindSymbolPositionAsync(currentFile, currentName, symbolKind, ct);
            if (pos is null)
                return $"Symbol '{symbolName}' not found in {filePath}";

            chain.Add((currentName, symbolKind ?? "Class", currentFile, pos.Value.line + 1));
            visited.Add(currentName);

            while (true)
            {
                ct.ThrowIfCancellationRequested();

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

                var baseType = afterColon
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t =>
                    {
                        var s = t.Split('<')[0].Trim();
                        var parenIdx = s.IndexOf('(');
                        return parenIdx >= 0 ? s[..parenIdx].Trim() : s;
                    })
                    .FirstOrDefault(t => t.Length > 0);

                if (baseType is null || !visited.Add(baseType)) break;

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

    // -- Call hierarchy fallbacks --

    private async Task<string> IncomingCallsFallback(SymbolContext ctx, string symbolName, CancellationToken ct)
    {
        // Use references to find all call sites, then determine containing method for each
        var @params = MakePositionParams(ctx);
        @params["context"] = new JObject { ["includeDeclaration"] = false };

        var refs = await _lsp.RequestAsync("textDocument/references", @params, ct);
        if (refs is not JArray refArray || refArray.Count == 0)
            return $"No incoming calls found for '{symbolName}'.";

        var callers = new List<(string name, string kind, string path, int line)>();
        var seen = new HashSet<string>();
        var symbolCache = new Dictionary<string, JArray?>();

        foreach (var r in refArray)
        {
            var refUri = r["uri"]?.ToString();
            var refLine = r["range"]?["start"]?["line"]?.Value<int>();
            if (refUri is null || refLine is null) continue;

            var refPath = LspClient.UriToPath(refUri);

            if (!symbolCache.TryGetValue(refUri, out var symbols))
            {
                await _lsp.EnsureDocumentOpenAsync(refPath, ct);
                var symResult = await _lsp.RequestAsync("textDocument/documentSymbol", new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = refUri }
                }, ct);
                symbols = symResult as JArray;
                symbolCache[refUri] = symbols;
            }

            if (symbols is null) continue;

            var container = FindContainingMethod(symbols, refLine.Value);
            if (container is null) continue;

            var name = container["name"]?.ToString() ?? "?";
            var kind = LspClient.SymbolKindName(container["kind"]?.Value<int>() ?? 0);
            var range = container["selectionRange"] ?? container["range"];
            var line = (range?["start"]?["line"]?.Value<int>() ?? 0) + 1;

            var key = $"{name}:{refPath}:{line}";
            if (!seen.Add(key)) continue;

            callers.Add((name, kind, refPath, line));
        }

        if (callers.Count == 0)
            return $"No incoming calls found for '{symbolName}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {callers.Count} caller(s) of '{symbolName}':");
        sb.AppendLine();
        foreach (var (name, kind, path, line) in callers)
            sb.AppendLine($"  {name} ({kind}) - {path}:{line}");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> OutgoingCallsFallback(SymbolContext ctx, string filePath, string symbolName, CancellationToken ct)
    {
        // Find the method's range via documentSymbol, then scan body for call sites
        var symResult = await _lsp.RequestAsync("textDocument/documentSymbol", new JObject
        {
            ["textDocument"] = new JObject { ["uri"] = ctx.Uri }
        }, ct);

        if (symResult is not JArray symbols)
            return $"No outgoing calls found from '{symbolName}'.";

        var method = FindContainingMethod(symbols, ctx.Line);
        if (method is null)
            return $"No outgoing calls found from '{symbolName}'.";

        var methodRange = method["range"];
        var startLine = methodRange?["start"]?["line"]?.Value<int>() ?? 0;
        var endLine = methodRange?["end"]?["line"]?.Value<int>() ?? 0;

        var lines = await File.ReadAllLinesAsync(filePath, ct);

        // Find call-site patterns in method body
        var callPattern = new Regex(@"(?<![.\w])(\w+)\s*[<\(]");
        var memberCallPattern = new Regex(@"\.(\w+)\s*[<\(]");
        var keywords = new HashSet<string> {
            "if", "for", "foreach", "while", "switch", "catch", "using", "return",
            "new", "typeof", "nameof", "sizeof", "await", "throw", "lock", "fixed",
            "checked", "unchecked", "default", "stackalloc", "var", "int", "string",
            "bool", "double", "float", "long", "byte", "short", "char", "decimal",
            "object", "void", "null", "true", "false", "this", "base",
        };

        var uniqueCalls = new Dictionary<string, (int line, int col)>();

        for (var i = startLine; i <= endLine && i < lines.Length; i++)
        {
            foreach (Match m in memberCallPattern.Matches(lines[i]))
            {
                var name = m.Groups[1].Value;
                uniqueCalls.TryAdd(name, (i, m.Groups[1].Index));
            }
            foreach (Match m in callPattern.Matches(lines[i]))
            {
                var name = m.Groups[1].Value;
                if (keywords.Contains(name)) continue;
                uniqueCalls.TryAdd(name, (i, m.Groups[1].Index));
            }
        }

        if (uniqueCalls.Count == 0)
            return $"No outgoing calls found from '{symbolName}'.";

        // Resolve each unique call via textDocument/definition
        var results = new List<(string name, string kind, string path, int line)>();
        var seen = new HashSet<string>();

        foreach (var (name, (line, col)) in uniqueCalls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var defResult = await _lsp.RequestAsync("textDocument/definition", new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = ctx.Uri },
                    ["position"] = new JObject { ["line"] = line, ["character"] = col },
                }, ct);

                // Extract the first definition location
                JToken? def = defResult is JArray arr && arr.Count > 0 ? arr[0] : defResult;
                if (def is null) continue;

                var defUri = def["uri"]?.ToString() ?? def["targetUri"]?.ToString();
                var defRange = def["range"]?["start"] ?? def["targetRange"]?["start"];
                if (defUri is null || defRange is null) continue;

                var defPath = LspClient.UriToPath(defUri);
                var defLine = (defRange["line"]?.Value<int>() ?? 0) + 1;

                var key = $"{defPath}:{defLine}";
                if (!seen.Add(key)) continue;

                // Get the actual symbol kind at the definition
                await _lsp.EnsureDocumentOpenAsync(defPath, ct);
                var defSymbols = await _lsp.RequestAsync("textDocument/documentSymbol", new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = defUri }
                }, ct);

                var kind = "Method";
                if (defSymbols is JArray defSymArr)
                {
                    var defSym = FindContainingMethod(defSymArr, defLine - 1);
                    if (defSym is not null)
                        kind = LspClient.SymbolKindName(defSym["kind"]?.Value<int>() ?? 6);
                }

                results.Add((name, kind, defPath, defLine));
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip unresolvable calls */ }
        }

        if (results.Count == 0)
            return $"No outgoing calls found from '{symbolName}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} call(s) from '{symbolName}':");
        sb.AppendLine();
        foreach (var (name, kind, path, defLine) in results)
            sb.AppendLine($"  {name} ({kind}) - {path}:{defLine}");
        return sb.ToString().TrimEnd();
    }

    private static JToken? FindContainingMethod(JArray symbols, int line)
    {
        foreach (var sym in symbols)
        {
            var range = sym["range"];
            if (range is null) continue;

            var startLine = range["start"]?["line"]?.Value<int>() ?? -1;
            var endLine = range["end"]?["line"]?.Value<int>() ?? -1;

            if (line < startLine || line > endLine) continue;

            // Check children first (innermost match)
            if (sym["children"] is JArray children)
            {
                var child = FindContainingMethod(children, line);
                if (child is not null) return child;
            }

            // Return if it's a method, constructor, or function
            var kind = sym["kind"]?.Value<int>() ?? 0;
            if (kind is 6 or 9 or 12) // Method, Constructor, Function
                return sym;
        }
        return null;
    }

    // -- Type hierarchy fallbacks --

    private async Task<string> SupertypesFallback(string filePath, string symbolName, int line, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(filePath, ct);
        if (line >= lines.Length)
            return $"No supertypes found for '{symbolName}'.";

        var declLine = lines[line].Trim();

        var colonIdx = declLine.IndexOf(':');
        if (colonIdx < 0)
            return $"No supertypes found for '{symbolName}' (no base type in declaration).";

        var afterColon = declLine[(colonIdx + 1)..].Trim();
        var braceIdx = afterColon.IndexOf('{');
        if (braceIdx >= 0) afterColon = afterColon[..braceIdx];
        var whereIdx = afterColon.IndexOf(" where ", StringComparison.Ordinal);
        if (whereIdx >= 0) afterColon = afterColon[..whereIdx];

        var baseTypes = afterColon
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Split('<')[0].Trim())
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

    private async Task<string> SubtypesFallback(string uri, string symbolName, (int line, int character) pos, string? filter, int limit, CancellationToken ct)
    {
        var refsResult = await _lsp.RequestAsync("textDocument/references", new JObject
        {
            ["textDocument"] = new JObject { ["uri"] = uri },
            ["position"] = new JObject { ["line"] = pos.line, ["character"] = pos.character },
            ["context"] = new JObject { ["includeDeclaration"] = false },
        }, ct);

        if (refsResult is not JArray refs || refs.Count == 0)
            return $"No subtypes found for '{symbolName}'.";

        var filterRegex = filter is not null ? new Regex(filter, RegexOptions.IgnoreCase) : null;
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
            var match = Regex.Match(lineText, @"(?:class|struct|record|interface)\s+(\w+)");
            if (!match.Success) continue;

            var typeName = match.Groups[1].Value;
            if (typeName == symbolName) continue;

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

        sb.Insert(0, FormatFilteredHeader($"Subtypes of '{symbolName}' (", shown, matched, total, filter) + "\n\n");
        return sb.ToString().TrimEnd();
    }

    // -- Type resolution --

    private async Task<(string kind, string path, int line)?> ResolveTypeLocation(
        string typeName, CancellationToken ct, string? contextFilePath = null)
    {
        var isQualified = typeName.Contains('.');
        var shortName = isQualified ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;

        if (isQualified)
        {
            var qualifiedResult = await FindTypeSymbol(typeName, shortName, ct, contextFilePath);
            if (qualifiedResult is not null) return qualifiedResult;
        }

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

            var symKind = sym["kind"]?.Value<int>() ?? 0;
            if (symKind is not (5 or 10 or 11 or 23)) continue;

            var kind = LspClient.SymbolKindName(symKind);
            var loc = sym["location"];
            var path = loc?["uri"] is JToken u ? LspClient.UriToPath(u.ToString()) : null;
            var symLine = loc?["range"]?["start"]?["line"]?.Value<int>() ?? 0;

            if (path is null) continue;

            if (contextSegment is not null &&
                path.Contains(contextSegment, StringComparison.OrdinalIgnoreCase))
            {
                sameProjectMatch ??= (kind, path, symLine);
            }

            anyMatch ??= (kind, path, symLine);
        }

        return sameProjectMatch ?? anyMatch;
    }
}
