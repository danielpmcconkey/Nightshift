using Microsoft.Extensions.Hosting;
using Nightshift.Engine.Data;
using Serilog;

namespace Nightshift.Engine.Engine;

public class EngineWorker : BackgroundService
{
    private readonly EngineConfigRepository _engineConfig;
    private readonly TaskQueueRepository _taskQueue;
    private readonly RunHistoryRepository _runHistory;
    private readonly WorkflowRepository _workflowRepo;
    private readonly ProjectRepository _projectRepo;
    private readonly StepHandler _stepHandler;
    private static readonly ILogger Log = Serilog.Log.ForContext<EngineWorker>();

    // Paths — resolved relative to the Nightshift repo root
    private readonly string _basePath;
    private readonly string _blueprintBasePath;

    public EngineWorker(
        EngineConfigRepository engineConfig,
        TaskQueueRepository taskQueue,
        RunHistoryRepository runHistory,
        WorkflowRepository workflowRepo,
        ProjectRepository projectRepo,
        StepHandler stepHandler)
    {
        _engineConfig = engineConfig;
        _taskQueue = taskQueue;
        _runHistory = runHistory;
        _workflowRepo = workflowRepo;
        _projectRepo = projectRepo;
        _stepHandler = stepHandler;

        // Base path is the Nightshift repo root
        _basePath = Environment.GetEnvironmentVariable("NIGHTSHIFT_BASE_PATH")
            ?? "/workspace/Nightshift";
        _blueprintBasePath = Path.Combine(_basePath, "blueprints");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("Nightshift engine starting up");
        Log.Information("Base path: {BasePath}", _basePath);
        Log.Information("Blueprint path: {BlueprintPath}", _blueprintBasePath);

        try
        {
            await Startup(stoppingToken);
            await MainLoop(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            Log.Information("Nightshift engine shutting down (cancellation requested)");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Nightshift engine crashed");
            throw;
        }

        Log.Information("Nightshift engine stopped");
    }

    private async Task Startup(CancellationToken ct)
    {
        // Recover orphaned tasks
        Log.Information("Checking for orphaned tasks...");
        await _taskQueue.RecoverOrphaned(ct);

        // Cleanup old run history
        Log.Information("Cleaning up old run history...");
        await _runHistory.CleanupOldEntries(retentionDays: 30, batchSize: 1000, ct);

        // Validate all workflows
        Log.Information("Validating workflows...");
        var workflows = await _workflowRepo.GetAll(ct);
        foreach (var workflow in workflows)
        {
            WorkflowValidator.Validate(workflow, _blueprintBasePath);
        }

        // Validate all project repo paths
        Log.Information("Validating project repo paths...");
        var projects = await _projectRepo.GetAll(ct);
        foreach (var project in projects)
        {
            _projectRepo.ValidateRepoPath(project.RepoPath);
            Log.Information("Project '{ProjectName}': {RepoPath}", project.Name, project.RepoPath);
        }

        Log.Information("Startup checks complete");
    }

    private async Task MainLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Check engine_enabled
            if (!await _engineConfig.IsEngineEnabled(ct))
            {
                Log.Information("Engine disabled — exiting");
                return;
            }

            // Try to claim a task
            var (task, conn, tx) = await _taskQueue.ClaimNext(ct);

            if (task is null)
            {
                // Check for unblocked cards that need their first step enqueued
                var activated = await _taskQueue.ActivateUnblockedCards(_workflowRepo, _projectRepo, ct);
                if (activated > 0)
                    continue; // New tasks enqueued — loop back and claim immediately

                Log.Debug("No pending unblocked cards — waiting 60s before recheck");
                await Task.Delay(TimeSpan.FromSeconds(60), ct);

                if (!await _engineConfig.IsEngineEnabled(ct))
                {
                    Log.Information("Engine disabled during idle poll — exiting");
                    return;
                }

                continue;
            }

            try
            {
                var shouldContinue = await _stepHandler.ProcessTask(
                    task, conn, tx, _blueprintBasePath, _basePath, ct);

                if (!shouldContinue)
                {
                    Log.Information("Engine disabled during processing — exiting");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unhandled error processing task {TaskId} for card {CardId}",
                    task.Id, task.CardId);

                // Try to fail the task so it doesn't stay claimed forever
                try
                {
                    await using var failTx = await conn.BeginTransactionAsync(ct);
                    await _taskQueue.Fail(task.Id, failTx, ct);
                    await failTx.CommitAsync(ct);
                }
                catch (Exception failEx)
                {
                    Log.Error(failEx, "Failed to mark task {TaskId} as failed", task.Id);
                }
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }
    }
}
