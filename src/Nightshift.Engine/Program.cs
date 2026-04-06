using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nightshift.Engine.Agents;
using Nightshift.Engine.Data;
using Nightshift.Engine.Engine;
using Npgsql;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Npgsql", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .Enrich.WithThreadId()
    .Enrich.WithMachineName()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/nightshift-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate:
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] {Message:lj}{NewLine}{Exception}",
        retainedFileCountLimit: 30)
    .Filter.ByExcluding(e =>
        e.MessageTemplate.Text.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        e.MessageTemplate.Text.Contains("Password", StringComparison.OrdinalIgnoreCase))
    .CreateLogger();

try
{
    Log.Information("Nightshift engine starting");

    var connectionString = Environment.GetEnvironmentVariable("NIGHTSHIFT_CONNECTION_STRING")
        ?? throw new InvalidOperationException(
            "NIGHTSHIFT_CONNECTION_STRING environment variable is required");

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog();

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    var dataSource = dataSourceBuilder.Build();
    builder.Services.AddSingleton(dataSource);

    builder.Services.AddSingleton<CardRepository>();
    builder.Services.AddSingleton<TaskQueueRepository>();
    builder.Services.AddSingleton<EngineConfigRepository>();
    builder.Services.AddSingleton<WorkflowRepository>();
    builder.Services.AddSingleton<BlockerRepository>();
    builder.Services.AddSingleton<RunHistoryRepository>();
    builder.Services.AddSingleton<ProjectRepository>();

    builder.Services.AddSingleton<ArtifactManager>();
    builder.Services.AddSingleton<PromptBuilder>();
    builder.Services.AddSingleton<OutcomeParser>();
    builder.Services.AddSingleton<AgentInvoker>();

    builder.Services.AddSingleton<ReviewLoopHandler>();
    builder.Services.AddSingleton<StepHandler>();

    builder.Services.AddHostedService<EngineWorker>();

    builder.Services.Configure<HostOptions>(options =>
    {
        options.ShutdownTimeout = TimeSpan.FromSeconds(60);
    });

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Nightshift engine terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
