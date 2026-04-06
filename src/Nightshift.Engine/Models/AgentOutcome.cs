namespace Nightshift.Engine.Models;

public enum AgentOutcome
{
    Success,
    Fail,
    Approved,
    Conditional,
    JudgmentNeeded
}

public static class AgentOutcomeExtensions
{
    public static AgentOutcome? Parse(string? outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome)) return null;

        return outcome.Trim().ToUpperInvariant() switch
        {
            "SUCCESS" => AgentOutcome.Success,
            "FAIL" => AgentOutcome.Fail,
            "APPROVED" => AgentOutcome.Approved,
            "CONDITIONAL" => AgentOutcome.Conditional,
            "JUDGMENT_NEEDED" => AgentOutcome.JudgmentNeeded,
            _ => null
        };
    }

    public static string ToOutcomeString(this AgentOutcome outcome) => outcome switch
    {
        AgentOutcome.Success => "SUCCESS",
        AgentOutcome.Fail => "FAIL",
        AgentOutcome.Approved => "APPROVED",
        AgentOutcome.Conditional => "CONDITIONAL",
        AgentOutcome.JudgmentNeeded => "JUDGMENT_NEEDED",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };
}
