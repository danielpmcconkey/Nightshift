namespace Nightshift.Engine.Models;

public class RunHistoryEntry
{
    public long Id { get; set; }
    public int CardId { get; set; }
    public string StepName { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Outcome { get; set; }
    public string? Notes { get; set; }
}
