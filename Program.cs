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

builder.Services.AddSingleton(new LspClientOptions
{
    SolutionPath = Environment.GetEnvironmentVariable("SHARPLSMCP_SOLUTION"),
    WorkspaceRoot = Environment.GetEnvironmentVariable("SHARPLSMCP_ROOT"),
    TimeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("SHARPLSMCP_TIMEOUT"), out var t) ? t : 60,
});

builder.Services.AddSingleton<LspClient>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LspClient>());

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<LspTools>();

await builder.Build().RunAsync();
