namespace Nightshift.Engine.Models;

public class Project
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RepoPath { get; set; } = string.Empty;
    public int? WorkflowId { get; set; }
    public string AddendaSubpath { get; set; } = "DSWF";
    public DateTime CreatedAt { get; set; }
}
