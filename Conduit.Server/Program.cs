using System.CommandLine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conduit;
using ZLogger;

var httpOption = new Option<bool>("--http")
{
    Description = "Run the MCP server over HTTP instead of stdio.",
};

var urlOption = new Option<string?>("--url")
{
    Description = "HTTP listen URL when running with --http.",
};

var portOption = new Option<ushort?>("--port")
{
    Description = "HTTP port when running with --http. Uses 127.0.0.1 unless --url is provided.",
};

var versionOption = new Option<bool>("--version")
{
    Description = "Print the Conduit server version and exit.",
};

var updateOption = new Option<bool>("--update", "-U")
{
    Description = "Download the latest GitHub release executable and replace the current one.",
};

var rootCommand = new RootCommand("Conduit MCP server")
{
    httpOption,
    urlOption,
    portOption,
    updateOption,
    versionOption,
};

rootCommand.SetAction(async parseResult =>
    {
        if (parseResult.GetValue(versionOption))
        {
            Console.Out.WriteLine(ConduitServerMetadata.GetDisplayVersion());
            return 0;
        }

        if (parseResult.GetValue(updateOption))
        {
            try
            {
                var result = await SelfUpdater.UpdateAsync(CancellationToken.None);
                Console.Out.WriteLine(result.Message);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Update failed: {exception.Message}");
                return 1;
            }
        }

        var http = parseResult.GetValue(httpOption);
        var url = parseResult.GetValue(urlOption);
        var port = parseResult.GetValue(portOption);
        if (http)
            await RunHttpAsync(url, port);
        else
            await RunStdioAsync();

        return 0;
    }
);

return await rootCommand.Parse(args).InvokeAsync();

static async Task RunStdioAsync()
{
    var builder = Host.CreateApplicationBuilder();
    ConfigureCommon(builder.Configuration, builder.Logging, builder.Services);
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<UnityTools>(CreateToolSerializerOptions());

    await builder.Build().RunAsync();
}

static async Task RunHttpAsync(string? url, ushort? port)
{
    var builder = WebApplication.CreateBuilder();
    if (ResolveHttpUrl(url, port) is { Length: > 0 } resolvedUrl)
        builder.WebHost.UseUrls(resolvedUrl);

    ConfigureCommon(builder.Configuration, builder.Logging, builder.Services);
    builder.Services
        .AddMcpServer()
        .WithHttpTransport()
        .WithTools<UnityTools>(CreateToolSerializerOptions());

    var app = builder.Build();
    app.MapMcp();
    await app.RunAsync();
}

static string? ResolveHttpUrl(string? url, ushort? port)
{
    if (url is { Length: > 0 })
        return url;

    if (port is > 0)
        return $"http://127.0.0.1:{port.Value}";

    return Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
}

static void ConfigureCommon(
    ConfigurationManager configuration,
    ILoggingBuilder logging,
    IServiceCollection services
)
{
    configuration.AddEnvironmentVariables(prefix: ConduitEnvironment.Prefix);

    logging.ClearProviders();
    logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    var serverLogPath = ConduitHostPaths.GetServerLogPath();
    if (Path.GetDirectoryName(serverLogPath) is { Length: > 0 } logDirectoryPath)
        Directory.CreateDirectory(logDirectoryPath);

    logging.AddZLoggerFile(serverLogPath);

    var hostConfiguration = configuration.Get<ConduitHostConfiguration>() ?? new();
    services.AddSingleton(hostConfiguration);
    services.AddSingleton(hostConfiguration.ToOptions());
    services.AddSingleton(TimeProvider.System);
    services.AddSingleton<RecentProjectStore>();
    services.AddSingleton<UnityProjectRegistry>();
    services.AddSingleton<UnityBridgeClient>();
    services.AddSingleton<UnityProjectEnvironmentInspector>();
    services.AddSingleton<UnityEditorProcessController>();
    services.AddSingleton<UnityProjectOperations>();
}

static JsonSerializerOptions CreateToolSerializerOptions()
    => new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        TypeInfoResolver = ConduitJsonContext.Default,
    };
