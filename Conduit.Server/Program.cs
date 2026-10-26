using System.CommandLine;
using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conduit;
using ZLogger;

const string AgentInstructionsText =
    """
    # Unity MCP usage instructions
    Use the Unity MCP tools to prototype solutions, validate code compilation and run tests.
    Invoke the `restart` tool in case of instability.
    Don't build the Unity solution manually; simply call `refresh_asset_database` after making any code changes.
    When dealing with assets and GameObjects, `search`, `show`, `to_json`, `from_json_overwrite`, `find_missing_scripts`, `get_dependencies`, `find_references_to` and `reimport_assets` are your friends.
    When working with code, you can use `reflect` to browse types and members, and `view_burst_asm` to validate Burst-compiled code.
    Use `help` once to get instructions about the common query format used in `search`, `show`, `Search<T>` calls in `execute_code`, etc.
    Prototyping with `detour`: Use this tool to replace a method's implementation at runtime. You can *patch a method without reloading the domain*, and then `execute_code` to check whether the intended behavior works. This is extremely useful for prototyping, validating ideas without committing to them, and debugging.
    Never run the Unity executable manually. Never kill the Unity process. Use the `restart` command instead, this tool is robust and creates the correct environment for Unity MCP.
    Never build the Unity solution manually. Never `dotnet build Assembly-CSharp.csproj` to validate your changes. Call `refresh_asset_database` after making code changes instead, this is the correct way to import changed assets and trigger script compilation.
    """;

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

var tokenFileOption = new Option<string?>("--token-file")
{
    Description = "Enable HTTP bearer token authentication using a token file; read once at startup.",
};

var tokenSha256FileOption = new Option<string?>("--token-sha256-file")
{
    Description = "Enable HTTP bearer token authentication using a SHA-256 fingerprint file (hex). Alternative to --token-file.",
};

var protectProcessOption = new Option<bool>("--protect-process")
{
    Description = "Block ordinary memory inspection and crash dumps on Linux. Requires --http and DOTNET_EnableDiagnostics=0 at startup.",
};

var updateOption = new Option<bool>("--update", "-U")
{
    Description = "Download the latest GitHub release executable and replace the current one.",
};

var printAgentInstructionsOption = new Option<bool>("--print-agent-instructions")
{
    Description = "Print Unity MCP usage instructions when run from a Unity project.",
};

var rootCommand = new RootCommand("Conduit MCP server")
{
    httpOption,
    urlOption,
    portOption,
    tokenFileOption,
    tokenSha256FileOption,
    protectProcessOption,
    updateOption,
    printAgentInstructionsOption,
};

rootCommand.SetAction(async parseResult =>
    {
        if (parseResult.GetValue(printAgentInstructionsOption))
        {
            if (LooksLikeUnityProject(Environment.CurrentDirectory))
                Console.Out.WriteLine(AgentInstructionsText);

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
        var tokenFile = parseResult.GetValue(tokenFileOption);
        var tokenSha256File = parseResult.GetValue(tokenSha256FileOption);
        var protectProcess = parseResult.GetValue(protectProcessOption);
        if (http)
            return await RunHttpAsync(url, port, tokenFile, tokenSha256File, protectProcess);

        if (tokenFile is not null || tokenSha256File is not null || protectProcess)
        {
            Console.Error.WriteLine("--token-file, --token-sha256-file and --protect-process require --http.");
            return 1;
        }

        await RunStdioAsync();

        return 0;
    }
);

return await rootCommand.Parse(args).InvokeAsync();

static bool LooksLikeUnityProject(string path) =>
    Directory.Exists(Path.Combine(path, "Assets"))
    && Directory.Exists(Path.Combine(path, "Packages"))
    && File.Exists(Path.Combine(path, "ProjectSettings", "ProjectVersion.txt"));

static async Task RunStdioAsync()
{
    var builder = Host.CreateApplicationBuilder(
        new HostApplicationBuilderSettings
        {
            Args = GetHostArguments(),
        }
    );
    ConfigureCommon(builder.Configuration, builder.Logging, builder.Services);
    builder.Services
        .AddMcpServer()
        .WithRequestFilters(filters =>
        {
            filters.AddCallToolFilter(ToolCallArgumentValidationFilter.Apply);
            filters.AddCallToolFilter(EditorIdleCloseToolFilter.Apply);
        })
        .WithStdioServerTransport()
        .WithTools<UnityTools>(CreateToolSerializerOptions());

    await builder.Build().RunAsync();
}

static async Task<int> RunHttpAsync(
    string? url,
    ushort? port,
    string? tokenFile,
    string? tokenSha256File,
    bool protectProcess)
{
    Func<HttpContext, RequestDelegate, Task> authenticate;
    try
    {
        if (protectProcess)
            ProcessProtection.Enable();

        authenticate = HttpTokenAuthentication.Load(tokenFile, tokenSha256File);
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException
        or PlatformNotSupportedException or Win32Exception)
    {
        Console.Error.WriteLine($"HTTP startup failed: {exception.Message}");
        return 1;
    }

    var builder = WebApplication.CreateBuilder(
        new WebApplicationOptions
        {
            Args = GetHostArguments(),
        }
    );
    if (ResolveHttpUrl(url, port) is { Length: > 0 } resolvedUrl)
        builder.WebHost.UseUrls(resolvedUrl);

    ConfigureCommon(builder.Configuration, builder.Logging, builder.Services);
    builder.Services
        .AddMcpServer()
        .WithRequestFilters(filters =>
        {
            filters.AddCallToolFilter(ToolCallArgumentValidationFilter.Apply);
            filters.AddCallToolFilter(EditorIdleCloseToolFilter.Apply);
        })
        .WithHttpTransport(options => options.Stateless = true)
        .WithTools<UnityTools>(CreateToolSerializerOptions());

    var app = builder.Build();
    app.Use(authenticate);
    app.MapMcp();
    await app.RunAsync();
    return 0;
}

static string? ResolveHttpUrl(string? url, ushort? port)
{
    if (url is { Length: > 0 })
        return url;

    if (port is > 0)
        return $"http://127.0.0.1:{port.Value}";

    return Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
}

static string[] GetHostArguments() =>
[
    // configuration is read once at startup; a recursive working-tree watcher provides no value.
    "--hostBuilder:reloadConfigOnChange=false",
];

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
    services.AddSingleton<UnityPlayerDiscovery>();
    services.AddSingleton<UnityBridgeClient>();
    services.AddSingleton<SnippetCompiler>();
    services.AddSingleton<DetourCompiler>();
    services.AddSingleton<UnityProjectEnvironmentInspector>();
    services.AddSingleton<UnityEditorProcessController>();
    services.AddSingleton<UnitySceneReloadPromptRecovery>();
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
