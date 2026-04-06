using System.Text.Json;

namespace Nightshift.Engine.Models;

public class Card
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Priority { get; set; }
    public string Status { get; set; } = "queued";
    public string? CurrentStep { get; set; }
    public JsonDocument? ConditionalCounts { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public int GetGateCount(string gateName)
    {
        if (ConditionalCounts is null) return 0;
        if (ConditionalCounts.RootElement.TryGetProperty(gateName, out var val))
            return val.GetInt32();
        return 0;
    }
}
