using System.Text.Json;
using Nightshift.Engine.Agents;
using Nightshift.Engine.Data;
using Nightshift.Engine.Models;
using Npgsql;
using Serilog;

namespace Nightshift.Engine.Engine;

public class StepHandler
{
    private readonly CardRepository _cardRepo;
    private readonly TaskQueueRepository _taskQueueRepo;
    private readonly BlockerRepository _blockerRepo;
    private readonly RunHistoryRepository _runHistoryRepo;
    private readonly WorkflowRepository _workflowRepo;
    private readonly ProjectRepository _projectRepo;
    private readonly AgentInvoker _agentInvoker;
    private readonly ArtifactManager _artifacts;
    private readonly ReviewLoopHandler _reviewLoopHandler;
    private readonly EngineConfigRepository _engineConfigRepo;
    private static readonly ILogger Log = Serilog.Log.ForContext<StepHandler>();

    public StepHandler(
        CardRepository cardRepo,
        TaskQueueRepository taskQueueRepo,
        BlockerRepository blockerRepo,
        RunHistoryRepository runHistoryRepo,
        WorkflowRepository workflowRepo,
        ProjectRepository projectRepo,
        AgentInvoker agentInvoker,
        ArtifactManager artifacts,
        ReviewLoopHandler reviewLoopHandler,
        EngineConfigRepository engineConfigRepo)
    {
        _cardRepo = cardRepo;
        _taskQueueRepo = taskQueueRepo;
        _blockerRepo = blockerRepo;
        _runHistoryRepo = runHistoryRepo;
        _workflowRepo = workflowRepo;
        _projectRepo = projectRepo;
        _agentInvoker = agentInvoker;
        _artifacts = artifacts;
        _reviewLoopHandler = reviewLoopHandler;
        _engineConfigRepo = engineConfigRepo;
    }

    /// <summary>
    /// Process a claimed task. Returns true if the engine should continue polling,
    /// false if the engine was disabled during processing.
    /// </summary>
    public async Task<bool> ProcessTask(TaskQueueItem task, NpgsqlConnection conn,
        NpgsqlTransaction claimTx, string blueprintBasePath, string basePath, CancellationToken ct)
    {
        // Commit the claim transaction immediately — we've got the task
        await claimTx.CommitAsync(ct);

        var card = await _cardRepo.GetById(task.CardId, ct);
        if (card is null)
        {
            Log.Error("Card {CardId} not found for task {TaskId}", task.CardId, task.Id);
            await FailTaskStandalone(task.Id, conn, ct);
            return true;
        }

        var project = await _projectRepo.GetById(card.ProjectId, ct);
        if (project is null)
        {
            Log.Error("Project {ProjectId} not found for card {CardId}", card.ProjectId, card.Id);
            await FailTaskStandalone(task.Id, conn, ct);
            return true;
        }

        _projectRepo.ValidateRepoPath(project.RepoPath);

        var workflow = await _workflowRepo.GetForProject(project, ct);
        if (workflow is null)
        {
            Log.Error("No workflow found for project {ProjectName}", project.Name);
            await FailTaskStandalone(task.Id, conn, ct);
            return true;
        }

        var step = workflow.Steps.Find(s => s.StepName == task.StepName);
        if (step is null)
        {
            Log.Error("Step '{StepName}' not found in workflow '{WorkflowName}'",
                task.StepName, workflow.Name);
            await FailTaskStandalone(task.Id, conn, ct);
            return true;
        }

        _artifacts.EnsureDirectories(basePath, card.Id);

        var modelId = _agentInvoker.ResolveModel(step.ModelTier);

        // Log run start (in its own mini-transaction)
        long runId;
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            runId = await _runHistoryRepo.LogStart(card.Id, step.StepName, modelId, tx, ct);
            await tx.CommitAsync(ct);
        }

        string? injectContext = null;

        // Inner loop: process the current step, then keep going until complete/blocked/disabled
        var currentTask = task;
        var currentStep = step;
        var currentRunId = runId;

        while (true)
        {
            // Invoke agent OUTSIDE transaction (long-running)
            Log.Information("Processing card {CardId} step {Step}", card.Id, currentStep.StepName);
            var agentResponse = await _agentInvoker.Invoke(
                card, project, currentStep, blueprintBasePath, basePath, injectContext, ct);

            // Log stdout for debugging (agent writes process artifact itself)
            if (agentResponse.RawOutput is not null)
            {
                var stdoutLogPath = Path.Combine(
                    _artifacts.GetProcessDir(basePath, card.Id),
                    $"{currentStep.StepName}.stdout.log");
                await File.WriteAllTextAsync(stdoutLogPath, agentResponse.RawOutput, ct);
            }

            // State mutation transaction
            await using var tx = await conn.BeginTransactionAsync(ct);

            // Log run completion
            await _runHistoryRepo.LogCompletion(currentRunId,
                agentResponse.Outcome?.ToOutcomeString() ?? "UNKNOWN",
                agentResponse.Notes, tx, ct);

            // Determine next action
            var outcome = agentResponse.Outcome;
            var outcomeStr = outcome?.ToOutcomeString();

            // Check if outcome is in allowed_outcomes
            var isAllowedOutcome = outcomeStr is not null &&
                currentStep.AllowedOutcomes.Contains(outcomeStr);

            // Route to Foreman on CONDITIONAL, FAIL, JUDGMENT_NEEDED, unknown,
            // or outcome not in allowed list
            var needsForeman = outcome is AgentOutcome.Conditional or AgentOutcome.Fail
                or AgentOutcome.JudgmentNeeded || outcome is null || !isAllowedOutcome;

            if (needsForeman)
            {
                await tx.CommitAsync(ct); // Commit run history before Foreman call

                Log.Information("Routing to Foreman: card={CardId} step={Step} outcome={Outcome}",
                    card.Id, currentStep.StepName, outcomeStr ?? "UNKNOWN");

                var loopResult = await _reviewLoopHandler.Handle(
                    card, project, currentStep, agentResponse, workflow,
                    blueprintBasePath, basePath, ct);

                // New transaction for state changes
                await using var tx2 = await conn.BeginTransactionAsync(ct);

                if (loopResult.Decision == ForemanDecision.Escalate)
                {
                    await _blockerRepo.Create(card.Id, currentStep.StepName,
                        agentResponse.FullResponse, loopResult.ForemanAssessment,
                        loopResult.BlockerContext, tx2, ct);
                    await _cardRepo.MarkBlocked(card.Id, tx2, ct);
                    await _taskQueueRepo.Fail(currentTask.Id, tx2, ct);
                    await tx2.CommitAsync(ct);
                    Log.Information("Card {CardId} BLOCKED at step {Step}", card.Id, currentStep.StepName);
                    return true;
                }

                // Retry path
                var nextStepName = loopResult.NextStep!;
                var counts = await _reviewLoopHandler.GetCounts(card.Id, ct);
                if (currentStep.IsReviewGate)
                    counts[currentStep.StepName] = counts.GetValueOrDefault(currentStep.StepName, 0) + 1;
                await _reviewLoopHandler.PersistCounts(card.Id, counts, tx2, ct);

                await _cardRepo.AdvanceStep(card.Id, nextStepName, tx2, ct);
                await _taskQueueRepo.Complete(currentTask.Id, tx2, ct);
                var newTaskId = await _taskQueueRepo.Enqueue(card.Id, nextStepName, tx2, ct);
                await tx2.CommitAsync(ct);

                injectContext = loopResult.InjectContext;

                // Check engine_enabled before continuing inner loop
                if (!await _engineConfigRepo.IsEngineEnabled(ct))
                {
                    Log.Information("Engine disabled — exiting after step {Step}", currentStep.StepName);
                    return false;
                }

                // Reload for next iteration
                card = (await _cardRepo.GetById(card.Id, ct))!;
                currentStep = workflow.Steps.Find(s => s.StepName == nextStepName)!;
                currentTask = new TaskQueueItem { Id = newTaskId, CardId = card.Id, StepName = nextStepName };

                // Log new run start
                await using var tx3 = await conn.BeginTransactionAsync(ct);
                currentRunId = await _runHistoryRepo.LogStart(card.Id, currentStep.StepName,
                    _agentInvoker.ResolveModel(currentStep.ModelTier), tx3, ct);
                await tx3.CommitAsync(ct);
                continue;
            }

            // Happy path — outcome is allowed and in transition_map
            var nextStep = currentStep.GetNextStep(outcomeStr!);

            if (nextStep == "COMPLETE")
            {
                await _cardRepo.MarkComplete(card.Id, tx, ct);
                await _taskQueueRepo.Complete(currentTask.Id, tx, ct);
                await tx.CommitAsync(ct);
                Log.Information("Card {CardId} COMPLETE", card.Id);
                return true;
            }

            if (nextStep is not null)
            {
                // Reset gate counter on APPROVED at a review gate
                if (currentStep.IsReviewGate && outcome == AgentOutcome.Approved)
                {
                    var counts = await _reviewLoopHandler.GetCounts(card.Id, ct);
                    counts[currentStep.StepName] = 0;
                    await _reviewLoopHandler.PersistCounts(card.Id, counts, tx, ct);
                }

                await _cardRepo.AdvanceStep(card.Id, nextStep, tx, ct);
                await _taskQueueRepo.Complete(currentTask.Id, tx, ct);
                var newTaskId2 = await _taskQueueRepo.Enqueue(card.Id, nextStep, tx, ct);
                await tx.CommitAsync(ct);

                // Check engine_enabled before continuing
                if (!await _engineConfigRepo.IsEngineEnabled(ct))
                {
                    Log.Information("Engine disabled — exiting after step {Step}", currentStep.StepName);
                    return false;
                }

                // Continue inner loop with next step
                card = (await _cardRepo.GetById(card.Id, ct))!;
                currentStep = workflow.Steps.Find(s => s.StepName == nextStep)!;
                currentTask = new TaskQueueItem { Id = newTaskId2, CardId = card.Id, StepName = nextStep };
                injectContext = null;

                await using var tx4 = await conn.BeginTransactionAsync(ct);
                currentRunId = await _runHistoryRepo.LogStart(card.Id, currentStep.StepName,
                    _agentInvoker.ResolveModel(currentStep.ModelTier), tx4, ct);
                await tx4.CommitAsync(ct);
                continue;
            }

            // No transition found — shouldn't happen if workflow is validated
            Log.Error("No transition for outcome '{Outcome}' at step '{Step}' — blocking card",
                outcomeStr, currentStep.StepName);
            await _cardRepo.MarkBlocked(card.Id, tx, ct);
            await _taskQueueRepo.Fail(currentTask.Id, tx, ct);
            await tx.CommitAsync(ct);
            return true;
        }
    }

    private async Task FailTaskStandalone(long taskId, NpgsqlConnection conn, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await _taskQueueRepo.Fail(taskId, tx, ct);
        await tx.CommitAsync(ct);
    }
}
