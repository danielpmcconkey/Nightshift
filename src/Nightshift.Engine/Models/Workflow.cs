namespace Nightshift.Engine.Models;

public class Workflow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public List<WorkflowStep> Steps { get; set; } = [];
}
