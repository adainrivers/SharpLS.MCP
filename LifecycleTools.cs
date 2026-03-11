using System.ComponentModel;
using ModelContextProtocol.Server;

namespace SharpLS.MCP;

[McpServerToolType]
public class LifecycleTools : ToolBase
{
    public LifecycleTools(LspClient lsp) : base(lsp) { }

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

    [McpServerTool(Name = "reload_file"), Description("Reload a single file from disk. Use when a file has been modified outside the editor and the server has stale contents. Much faster than restarting the LSP.")]
    public async Task<string> ReloadFile(
        [Description("Absolute path to the C# file to reload")] string filePath,
        CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(filePath))
                return $"File not found: {filePath}";

            await _lsp.ReloadDocumentAsync(filePath, ct);
            return $"Reloaded {Path.GetFileName(filePath)}";
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
}
