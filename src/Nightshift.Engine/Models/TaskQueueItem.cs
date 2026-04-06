namespace Nightshift.Engine.Models;

public class TaskQueueItem
{
    public long Id { get; set; }
    public int CardId { get; set; }
    public string StepName { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
