using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace SharpLS.MCP;

[McpServerToolType]
public class IntelliSenseTools : ToolBase
{
    public IntelliSenseTools(LspClient lsp) : base(lsp) { }

    [McpServerTool(Name = "get_completion"), Description("Get code completion suggestions at a symbol position. Useful for discovering available members, methods, and types.")]
    public async Task<string> GetCompletion(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the symbol to get completions at (positions cursor at the symbol)")] string symbolName,
        [Description("Kind of symbol: class, method, property, field, interface, enum, struct, etc.")] string? symbolKind = null,
        [Description("Max items to return. Default: 30")] int limit = 30,
        CancellationToken ct = default)
    {
        try
        {
            var (ctx, err) = await PrepareSymbolRequest(filePath, symbolName, symbolKind, ct);
            if (ctx is null) return err!;

            var @params = MakePositionParams(ctx);
            @params["context"] = new JObject { ["triggerKind"] = 1 }; // Invoked

            var result = await _lsp.RequestAsync("textDocument/completion", @params, ct);
            if (result is null) return "No completions available.";

            JArray items;
            if (result is JArray arr)
            {
                items = arr;
            }
            else if (result["items"] is JArray listItems)
            {
                items = listItems;
            }
            else
            {
                return "No completions available.";
            }

            if (items.Count == 0)
                return "No completions available.";

            var sb = new StringBuilder();
            var shown = 0;

            foreach (var item in items)
            {
                if (shown >= limit) break;

                var label = item["label"]?.ToString() ?? "?";
                var kindVal = item["kind"]?.Value<int>() ?? 0;
                var kindName = CompletionItemKindName(kindVal);
                var detail = item["detail"]?.ToString();
                var labelDetail = item["labelDetails"]?["detail"]?.ToString();
                var labelDesc = item["labelDetails"]?["description"]?.ToString();

                sb.Append($"  {label}");
                if (labelDetail is not null) sb.Append(labelDetail);
                sb.Append($" ({kindName})");
                if (detail is not null) sb.Append($" - {detail}");
                if (labelDesc is not null) sb.Append($" [{labelDesc}]");
                sb.AppendLine();
                shown++;
            }

            var header = shown < items.Count
                ? $"Showing {shown} of {items.Count} completion(s)"
                : $"Found {items.Count} completion(s)";
            sb.Insert(0, $"{header} at '{symbolName}':\n\n");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return FormatError(ex); }
    }

    [McpServerTool(Name = "get_signature_help"), Description("Get method signature overloads and parameter info at a symbol position. Useful for understanding method parameters.")]
    public async Task<string> GetSignatureHelp(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Name of the symbol (method/constructor call) to get signatures for")] string symbolName,
        [Description("Kind of symbol: method, constructor, etc.")] string? symbolKind = null,
        CancellationToken ct = default)
    {
        try
        {
            var (ctx, err) = await PrepareSymbolRequest(filePath, symbolName, symbolKind, ct);
            if (ctx is null) return err!;

            var result = await _lsp.RequestAsync("textDocument/signatureHelp", MakePositionParams(ctx), ct);

            if (result is null)
                return $"No signature help available for '{symbolName}'.";

            var signatures = result["signatures"] as JArray;
            if (signatures is null || signatures.Count == 0)
                return $"No signatures found for '{symbolName}'.";

            var activeSignature = result["activeSignature"]?.Value<int>() ?? 0;
            var activeParam = result["activeParameter"]?.Value<int>() ?? 0;

            var sb = new StringBuilder();
            sb.AppendLine($"Signature(s) for '{symbolName}' ({signatures.Count} overload(s)):");
            sb.AppendLine();

            for (var i = 0; i < signatures.Count; i++)
            {
                var sig = signatures[i];
                var label = sig["label"]?.ToString() ?? "?";
                var isActive = i == activeSignature;
                var sigActiveParam = sig["activeParameter"]?.Value<int>() ?? activeParam;

                sb.Append(isActive ? "  >> " : "     ");
                sb.AppendLine(label);

                // Documentation
                var doc = ExtractMarkupContent(sig["documentation"]);
                if (doc is not null)
                    sb.AppendLine($"     {doc}");

                // Parameters
                var parameters = sig["parameters"] as JArray;
                if (parameters is not null && parameters.Count > 0)
                {
                    for (var p = 0; p < parameters.Count; p++)
                    {
                        var param = parameters[p];
                        var paramLabel = param["label"] is JArray offsets
                            ? label.Substring(offsets[0]!.Value<int>(), offsets[1]!.Value<int>() - offsets[0]!.Value<int>())
                            : param["label"]?.ToString() ?? "?";
                        var paramDoc = ExtractMarkupContent(param["documentation"]);
                        var marker = (isActive && p == sigActiveParam) ? "*" : " ";

                        sb.Append($"     {marker} {paramLabel}");
                        if (paramDoc is not null) sb.Append($" - {paramDoc}");
                        sb.AppendLine();
                    }
                }

                if (i < signatures.Count - 1) sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return FormatError(ex); }
    }

    private static string? ExtractMarkupContent(JToken? token)
    {
        if (token is null) return null;
        if (token is JValue v) return v.ToString();
        if (token["value"] is JToken val) return val.ToString();
        return null;
    }

    private static string CompletionItemKindName(int kind) => kind switch
    {
        1 => "Text", 2 => "Method", 3 => "Function", 4 => "Constructor",
        5 => "Field", 6 => "Variable", 7 => "Class", 8 => "Interface",
        9 => "Module", 10 => "Property", 11 => "Unit", 12 => "Value",
        13 => "Enum", 14 => "Keyword", 15 => "Snippet", 16 => "Color",
        17 => "File", 18 => "Reference", 19 => "Folder", 20 => "EnumMember",
        21 => "Constant", 22 => "Struct", 23 => "Event", 24 => "Operator",
        25 => "TypeParameter",
        _ => $"Unknown({kind})",
    };
}
