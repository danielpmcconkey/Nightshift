using System.Text.Json;

namespace Nightshift.Engine.Models;

public class AgentResponse
{
    public AgentOutcome? Outcome { get; set; }
    public string? Reason { get; set; }
    public string? Notes { get; set; }
    public string? InjectContext { get; set; }
    public string? RawOutput { get; set; }
    public int ExitCode { get; set; }
    public JsonDocument? FullResponse { get; set; }
}
