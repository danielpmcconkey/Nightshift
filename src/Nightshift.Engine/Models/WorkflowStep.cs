using System.Text.Json;

namespace Nightshift.Engine.Models;

public class WorkflowStep
{
    public int Id { get; set; }
    public int WorkflowId { get; set; }
    public string StepName { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public string BlueprintName { get; set; } = string.Empty;
    public string ModelTier { get; set; } = "sonnet";
    public int TimeoutSeconds { get; set; } = 1800;
    public string[] AllowedOutcomes { get; set; } = [];
    public JsonDocument? TransitionMap { get; set; }
    public bool IsReviewGate { get; set; }
    public string? ResponseStep { get; set; }
    public string? RewindTarget { get; set; }

    public string? GetNextStep(string outcome)
    {
        if (TransitionMap is null) return null;
        if (TransitionMap.RootElement.TryGetProperty(outcome, out var val))
            return val.GetString();
        return null;
    }
}
