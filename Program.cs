using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SharpLS.MCP;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// First positional arg = solution/project path
var solutionPath = args.FirstOrDefault(a => !a.StartsWith('-'));

builder.Services.AddSingleton(new LspClientOptions
{
    SolutionPath = solutionPath,
    TimeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("SHARPLSMCP_TIMEOUT"), out var t) ? t : 60,
});

builder.Services.AddSingleton<LspClient>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LspClient>());

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<NavigationTools>()
    .WithTools<HierarchyTools>()
    .WithTools<EditTools>()
    .WithTools<CodeActionTools>()
    .WithTools<IntelliSenseTools>()
    .WithTools<LifecycleTools>();

await builder.Build().RunAsync();
