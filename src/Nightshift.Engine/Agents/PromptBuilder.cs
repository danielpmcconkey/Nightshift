using Nightshift.Engine.Models;
using Serilog;

namespace Nightshift.Engine.Agents;

public class PromptBuilder
{
    private readonly ArtifactManager _artifacts;
    private static readonly ILogger Log = Serilog.Log.ForContext<PromptBuilder>();

    public PromptBuilder(ArtifactManager artifacts)
    {
        _artifacts = artifacts;
    }

    public async Task<string> BuildAgentPrompt(Card card, Project project, WorkflowStep step,
        string basePath, string? injectContext, CancellationToken ct)
    {
        var parts = new List<string>
        {
            "# Card",
            $"**Title:** {card.Title}",
            $"**Description:** {card.Description}",
            $"**Card ID:** {card.Id}",
            $"**Project:** {project.Name}",
            $"**Current Step:** {step.StepName}",
            "",
            $"**Working Directory:** {project.RepoPath}",
            ""
        };

        // Prior step artifacts
        var processDir = _artifacts.GetProcessDir(basePath, card.Id);
        if (Directory.Exists(processDir))
        {
            var artifacts = Directory.GetFiles(processDir, "*.json");
            if (artifacts.Length > 0)
            {
                parts.Add("# Prior Step Artifacts");
                foreach (var artifact in artifacts)
                {
                    var artifactStep = Path.GetFileNameWithoutExtension(artifact);
                    parts.Add($"- `{artifact}` ({artifactStep})");
                }
                parts.Add("");
            }
        }

        // Scratchpad
        var scratchpad = await _artifacts.ReadScratchpad(basePath, card.Id, ct);
        if (!string.IsNullOrWhiteSpace(scratchpad))
        {
            parts.Add("# Card Scratchpad (notes.md)");
            parts.Add(scratchpad);
            parts.Add("");
        }

        // Inject context from Foreman
        if (!string.IsNullOrWhiteSpace(injectContext))
        {
            parts.Add("# Additional Context (from Foreman)");
            parts.Add(injectContext);
            parts.Add("");
        }

        // Response format instructions
        parts.Add("# Response Format");
        parts.Add("You MUST write your response as a JSON file to:");
        parts.Add($"  `{_artifacts.GetProcessArtifactPath(basePath, card.Id, step.StepName)}`");
        parts.Add("");
        parts.Add("The JSON must include at minimum:");
        parts.Add("```json");
        parts.Add("{");
        parts.Add($"  \"outcome\": \"{string.Join(" | ", step.AllowedOutcomes)}\",");
        parts.Add("  \"reason\": \"brief explanation\",");
        parts.Add("  \"notes\": \"optional observations for downstream agents\"");
        parts.Add("}");
        parts.Add("```");
        parts.Add("");
        parts.Add("If you have observations useful for downstream agents, append them to:");
        parts.Add($"  `{_artifacts.GetScratchpadPath(basePath, card.Id)}`");

        return string.Join("\n", parts);
    }

    public async Task<string> BuildForemanPrompt(Card card, Project project,
        WorkflowStep failedStep, AgentResponse agentResponse,
        Dictionary<string, int> conditionalCounts, string basePath, CancellationToken ct)
    {
        var gateCount = failedStep.IsReviewGate
            ? conditionalCounts.GetValueOrDefault(failedStep.StepName, 0)
            : 0;

        var parts = new List<string>
        {
            "# Foreman Assessment Request",
            "",
            $"**Card:** {card.Title} (ID: {card.Id})",
            $"**Project:** {project.Name}",
            $"**Failed Step:** {failedStep.StepName}",
            $"**Agent Outcome:** {agentResponse.Outcome?.ToOutcomeString() ?? "UNKNOWN"}",
            $"**Agent Reason:** {agentResponse.Reason ?? "none provided"}",
            ""
        };

        if (failedStep.IsReviewGate)
        {
            parts.Add($"**Review gate loop count for {failedStep.StepName}:** {gateCount} of 3");
            parts.Add("");
        }

        if (!string.IsNullOrWhiteSpace(agentResponse.Notes))
        {
            parts.Add("# Agent Notes");
            parts.Add(agentResponse.Notes);
            parts.Add("");
        }

        if (agentResponse.RawOutput is not null)
        {
            // Truncate if huge
            var raw = agentResponse.RawOutput.Length > 4000
                ? agentResponse.RawOutput[..4000] + "\n... [truncated]"
                : agentResponse.RawOutput;
            parts.Add("# Full Agent Response");
            parts.Add("```");
            parts.Add(raw);
            parts.Add("```");
            parts.Add("");
        }

        // Scratchpad for context
        var scratchpad = await _artifacts.ReadScratchpad(basePath, card.Id, ct);
        if (!string.IsNullOrWhiteSpace(scratchpad))
        {
            parts.Add("# Card Scratchpad");
            parts.Add(scratchpad);
            parts.Add("");
        }

        parts.Add("# Your Response");
        parts.Add("Respond with JSON:");
        parts.Add("```json");
        parts.Add("{");
        parts.Add("  \"outcome\": \"RETRY | ESCALATE\",");
        parts.Add("  \"reason\": \"why this decision\",");
        parts.Add("  \"notes\": \"observations for the blocker record\",");
        parts.Add("  \"inject_context\": \"optional extra context for the next agent attempt\"");
        parts.Add("}");
        parts.Add("```");

        return string.Join("\n", parts);
    }

    public string LoadBlueprint(string blueprintBasePath, string blueprintName)
    {
        ValidateBlueprintName(blueprintName);
        var path = Path.Combine(blueprintBasePath, $"{blueprintName}.md");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Blueprint not found: {path}");
        return File.ReadAllText(path);
    }

    public string? LoadAddendum(string repoPath, string addendaSubpath, string blueprintName)
    {
        ValidateBlueprintName(blueprintName);
        var path = Path.Combine(repoPath, addendaSubpath, $"{blueprintName}.md");

        // Validate no path traversal
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith("/workspace/"))
        {
            Log.Warning("Addendum path traversal blocked: {Path}", path);
            return null;
        }

        if (!File.Exists(path)) return null;
        return File.ReadAllText(path);
    }

    private static void ValidateBlueprintName(string name)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9_-]+$"))
            throw new ArgumentException($"Invalid blueprint name: {name}. Must match [a-zA-Z0-9_-]+");
    }
}
