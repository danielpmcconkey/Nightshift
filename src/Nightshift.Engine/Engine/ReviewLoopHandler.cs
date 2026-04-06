using System.Text.Json;
using Nightshift.Engine.Agents;
using Nightshift.Engine.Data;
using Nightshift.Engine.Models;
using Npgsql;
using Serilog;

namespace Nightshift.Engine.Engine;

public enum ForemanDecision
{
    Retry,
    Escalate
}

public class ReviewLoopResult
{
    public ForemanDecision Decision { get; set; }
    public string? NextStep { get; set; }
    public string? InjectContext { get; set; }
    public string? BlockerContext { get; set; }
    public JsonDocument? ForemanAssessment { get; set; }
}

public class ReviewLoopHandler
{
    private readonly AgentInvoker _agentInvoker;
    private readonly CardRepository _cardRepo;
    private readonly BlockerRepository _blockerRepo;
    private readonly ArtifactManager _artifacts;
    private const int MaxLoopsPerGate = 3;
    private static readonly ILogger Log = Serilog.Log.ForContext<ReviewLoopHandler>();

    public ReviewLoopHandler(AgentInvoker agentInvoker, CardRepository cardRepo,
        BlockerRepository blockerRepo, ArtifactManager artifacts)
    {
        _agentInvoker = agentInvoker;
        _cardRepo = cardRepo;
        _blockerRepo = blockerRepo;
        _artifacts = artifacts;
    }

    public async Task<ReviewLoopResult> Handle(Card card, Project project, WorkflowStep failedStep,
        AgentResponse agentResponse, Workflow workflow, string blueprintBasePath, string basePath,
        CancellationToken ct)
    {
        var counts = await _cardRepo.GetConditionalCounts(card.Id, ct);

        // Invoke Foreman
        var foremanResponse = await _agentInvoker.InvokeForeman(
            card, project, failedStep, agentResponse, counts, blueprintBasePath, basePath, ct);

        // Determine Foreman's decision
        // Success → RETRY, Fail → ESCALATE, null → auto-escalate
        var foremanSaysRetry = foremanResponse.Outcome == AgentOutcome.Success;

        if (!foremanSaysRetry || foremanResponse.Outcome is null)
        {
            // ESCALATE or garbage response
            var context = foremanResponse.Outcome is null
                ? $"Foreman returned unparseable response at step '{failedStep.StepName}'"
                : $"Foreman escalated at step '{failedStep.StepName}': {foremanResponse.Notes}";

            Log.Warning("Foreman decision: ESCALATE for card {CardId} at {Step}", card.Id, failedStep.StepName);

            return new ReviewLoopResult
            {
                Decision = ForemanDecision.Escalate,
                BlockerContext = context,
                ForemanAssessment = foremanResponse.FullResponse
            };
        }

        // RETRY path — check loop cap
        if (failedStep.IsReviewGate)
        {
            var gateCount = counts.GetValueOrDefault(failedStep.StepName, 0);
            if (gateCount >= MaxLoopsPerGate)
            {
                var context = $"3-loop cap exceeded at review gate '{failedStep.StepName}' " +
                    $"(count: {gateCount}). Foreman wanted RETRY but engine hard cap prevents it.";
                Log.Warning("3-loop cap hit: card={CardId} gate={Gate} count={Count}",
                    card.Id, failedStep.StepName, gateCount);

                return new ReviewLoopResult
                {
                    Decision = ForemanDecision.Escalate,
                    BlockerContext = context,
                    ForemanAssessment = foremanResponse.FullResponse
                };
            }

            // Increment gate counter
            counts[failedStep.StepName] = gateCount + 1;
        }

        // Route to response_step (review gate) or back to the step itself
        string nextStep;
        if (failedStep.IsReviewGate && failedStep.ResponseStep is not null)
        {
            nextStep = failedStep.ResponseStep;
        }
        else
        {
            // Non-review-gate retry: just re-run the same step
            nextStep = failedStep.StepName;
        }

        // Clean stale downstream artifacts
        var downstreamSteps = _artifacts.GetDownstreamStepNames(workflow.Steps, failedStep.StepName);
        _artifacts.CleanStaleArtifacts(basePath, card.Id, downstreamSteps);

        Log.Information("Foreman decision: RETRY for card {CardId} → step {NextStep} (gate count: {Counts})",
            card.Id, nextStep, JsonSerializer.Serialize(counts));

        return new ReviewLoopResult
        {
            Decision = ForemanDecision.Retry,
            NextStep = nextStep,
            InjectContext = foremanResponse.InjectContext,
            ForemanAssessment = foremanResponse.FullResponse
        };
    }

    /// <summary>
    /// Persist updated conditional counts within the caller's transaction.
    /// </summary>
    public async Task PersistCounts(int cardId, Dictionary<string, int> counts,
        NpgsqlTransaction tx, CancellationToken ct)
    {
        await _cardRepo.UpdateConditionalCounts(cardId, counts, tx, ct);
    }

    public async Task<Dictionary<string, int>> GetCounts(int cardId, CancellationToken ct)
    {
        return await _cardRepo.GetConditionalCounts(cardId, ct);
    }
}
