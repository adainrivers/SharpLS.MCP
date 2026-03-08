using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace SharpLS.MCP;

[McpServerToolType]
public class CodeActionTools : ToolBase
{
    public CodeActionTools(LspClient lsp) : base(lsp) { }

    [McpServerTool(Name = "get_code_actions"), Description("List available code actions (quick fixes, refactorings) for a line or range in a C# file.")]
    public async Task<string> GetCodeActions(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Start line (1-based)")] int startLine,
        [Description("End line (1-based). Defaults to startLine")] int? endLine = null,
        [Description("Filter by kind: quickfix, refactor, refactor.extract, refactor.inline, refactor.rewrite, source, source.organizeImports, source.fixAll")] string? kind = null,
        CancellationToken ct = default)
    {
        try
        {
            var (actions, err) = await FetchCodeActions(filePath, startLine, endLine, kind, ct);
            if (actions is null) return err!;

            if (actions.Count == 0)
                return $"No code actions available at line {startLine}" +
                    (kind is not null ? $" (kind: {kind})" : "") + ".";

            var sb = new StringBuilder();
            sb.AppendLine($"Found {actions.Count} code action(s) at line {startLine}" +
                (endLine is not null && endLine != startLine ? $"-{endLine}" : "") + ":");
            sb.AppendLine();

            for (var i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                var title = action["title"]?.ToString() ?? "?";
                var actionKind = action["kind"]?.ToString() ?? "";
                var isPreferred = action["isPreferred"]?.Value<bool>() == true;
                var disabled = action["disabled"]?["reason"]?.ToString();

                sb.Append($"  {i + 1}. {title}");
                if (actionKind.Length > 0) sb.Append($" [{actionKind}]");
                if (isPreferred) sb.Append(" (preferred)");
                if (disabled is not null) sb.Append($" (disabled: {disabled})");
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return FormatError(ex); }
    }

    [McpServerTool(Name = "apply_code_action"), Description("Apply a code action (quick fix, refactoring) by its title. Use get_code_actions first to see available actions.")]
    public async Task<string> ApplyCodeAction(
        [Description("Absolute path to the C# file")] string filePath,
        [Description("Start line (1-based)")] int startLine,
        [Description("Title of the code action to apply (case-insensitive partial match)")] string actionTitle,
        [Description("End line (1-based). Defaults to startLine")] int? endLine = null,
        [Description("Filter by kind: quickfix, refactor, etc.")] string? kind = null,
        CancellationToken ct = default)
    {
        try
        {
            var (actions, err) = await FetchCodeActions(filePath, startLine, endLine, kind, ct);
            if (actions is null) return err!;

            // Find matching action by title
            JToken? match = null;
            foreach (var action in actions)
            {
                var title = action["title"]?.ToString() ?? "";
                if (title.Contains(actionTitle, StringComparison.OrdinalIgnoreCase))
                {
                    match = action;
                    break;
                }
            }

            if (match is null)
            {
                // Try exact match as fallback
                foreach (var action in actions)
                {
                    var title = action["title"]?.ToString() ?? "";
                    if (string.Equals(title, actionTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        match = action;
                        break;
                    }
                }
            }

            if (match is null)
                return $"No code action matching '{actionTitle}' found. Use get_code_actions to see available actions.";

            if (match["disabled"] is JToken disabled)
                return $"Code action '{match["title"]}' is disabled: {disabled["reason"]}.";

            // If action has a direct edit, use it. Otherwise resolve.
            var edit = match["edit"];
            if (edit is null)
            {
                var resolved = await _lsp.RequestAsync("codeAction/resolve", match as JObject ?? new JObject(), ct);
                if (resolved is null)
                    return $"Failed to resolve code action '{match["title"]}'.";
                edit = resolved["edit"];
            }

            if (edit is null)
            {
                // Some actions are command-only (no workspace edit)
                var command = match["command"];
                if (command is not null)
                    return $"Code action '{match["title"]}' requires command execution which is not supported. " +
                        "It does not provide a direct workspace edit.";
                return $"Code action '{match["title"]}' returned no edits.";
            }

            var matchTitle = match["title"]?.ToString() ?? actionTitle;
            var applied = await ApplyWorkspaceEditAsync(edit, ct);
            var summary = FormatWorkspaceEdit(edit, $"Applied '{matchTitle}'");
            return applied ? summary : $"(dry-run, edits NOT applied)\n\n{summary}";
        }
        catch (Exception ex) { return FormatError(ex); }
    }

    private async Task<(JArray? Actions, string? Error)> FetchCodeActions(
        string filePath, int startLine, int? endLine, string? kind, CancellationToken ct)
    {
        var (uri, err) = await PrepareDocumentRequest(filePath, ct);
        if (uri is null) return (null, err);

        // Fetch diagnostics for the range to pass as context
        JArray diagnosticsForRange = [];
        try
        {
            var diagResult = await _lsp.RequestAsync("textDocument/diagnostic", new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri },
            }, ct);

            if (diagResult?["items"] is JArray allDiags)
            {
                var sl = startLine - 1;
                var el = (endLine ?? startLine) - 1;
                foreach (var d in allDiags)
                {
                    var diagStart = d["range"]?["start"]?["line"]?.Value<int>() ?? -1;
                    var diagEnd = d["range"]?["end"]?["line"]?.Value<int>() ?? -1;
                    if (diagEnd >= sl && diagStart <= el)
                        diagnosticsForRange.Add(d);
                }
            }
        }
        catch { /* diagnostics fetch is best-effort */ }

        var context = new JObject { ["diagnostics"] = diagnosticsForRange };
        if (kind is not null)
            context["only"] = new JArray(kind);

        var sl0 = startLine - 1;
        var el0 = (endLine ?? startLine) - 1;

        var result = await _lsp.RequestAsync("textDocument/codeAction", new JObject
        {
            ["textDocument"] = new JObject { ["uri"] = uri },
            ["range"] = new JObject
            {
                ["start"] = new JObject { ["line"] = sl0, ["character"] = 0 },
                ["end"] = new JObject { ["line"] = el0 + 1, ["character"] = 0 },
            },
            ["context"] = context,
        }, ct);

        if (result is not JArray actions)
            return ([], null);

        return (actions, null);
    }
}
