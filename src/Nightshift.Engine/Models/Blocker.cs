using System.Text.Json;

namespace Nightshift.Engine.Models;

public class Blocker
{
    public int Id { get; set; }
    public int CardId { get; set; }
    public string StepName { get; set; } = string.Empty;
    public JsonDocument? AgentResponse { get; set; }
    public JsonDocument? ForemanAssessment { get; set; }
    public string? Context { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? Resolution { get; set; }
}
