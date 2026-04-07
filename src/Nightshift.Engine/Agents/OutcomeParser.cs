using System.Text.Json;
using Nightshift.Engine.Models;
using Serilog;

namespace Nightshift.Engine.Agents;

public class OutcomeParser
{
    private static readonly ILogger Log = Serilog.Log.ForContext<OutcomeParser>();

    /// <summary>
    /// Parse agent outcome from process artifact file. Stdout is not used for routing —
    /// agents MUST write their response to the artifact file. If the file doesn't exist,
    /// the agent failed to follow its contract.
    /// </summary>
    public AgentResponse Parse(string? stdout, int exitCode, string basePath, int cardId, string stepName)
    {
        var response = new AgentResponse
        {
            RawOutput = stdout,
            ExitCode = exitCode
        };

        var artifactPath = Path.Combine(basePath, "artifacts", cardId.ToString(), "process", $"{stepName}.json");

        if (!File.Exists(artifactPath))
        {
            Log.Warning("Agent did not write process artifact for card {CardId} step {Step} at {Path}",
                cardId, stepName, artifactPath);
            response.Outcome = null; // Will route to Foreman
            response.Reason = "Agent did not write process artifact file";
            return response;
        }

        string jsonSource;
        try
        {
            jsonSource = File.ReadAllText(artifactPath);
            Log.Debug("Parsing outcome from artifact file: {Path}", artifactPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read artifact file {Path}", artifactPath);
            response.Outcome = AgentOutcome.Fail;
            response.Reason = $"Failed to read process artifact: {ex.Message}";
            return response;
        }

        try
        {
            var doc = JsonDocument.Parse(jsonSource);
            response.FullResponse = doc;
            var root = doc.RootElement;

            if (root.TryGetProperty("outcome", out var outcomeElement) &&
                outcomeElement.ValueKind != JsonValueKind.Undefined)
            {
                var outcomeStr = outcomeElement.GetString();
                response.Outcome = AgentOutcomeExtensions.Parse(outcomeStr);

                if (root.TryGetProperty("reason", out var reasonEl))
                    response.Reason = reasonEl.GetString();
                if (root.TryGetProperty("notes", out var notesEl))
                    response.Notes = notesEl.GetString();
                if (root.TryGetProperty("inject_context", out var injectEl))
                    response.InjectContext = injectEl.GetString();
            }

            if (response.Outcome is null)
            {
                Log.Warning("Process artifact missing 'outcome' field for card {CardId} step {Step}",
                    cardId, stepName);
            }
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Process artifact is not valid JSON for card {CardId} step {Step}", cardId, stepName);
            response.Outcome = AgentOutcome.Fail;
            response.Reason = "Process artifact is not valid JSON";
        }

        return response;
    }

    /// <summary>
    /// Parse Foreman outcome from its artifact file. Same contract — file-based, no stdout parsing.
    /// </summary>
    public AgentResponse ParseForemanArtifact(string foremanArtifactPath, string? stdout, int exitCode)
    {
        var response = new AgentResponse { RawOutput = stdout, ExitCode = exitCode };

        if (!File.Exists(foremanArtifactPath))
        {
            Log.Warning("Foreman did not write artifact at {Path} — auto-escalating", foremanArtifactPath);
            response.Reason = "Foreman did not write process artifact file";
            return response; // null outcome → auto-escalate
        }

        string jsonSource;
        try
        {
            jsonSource = File.ReadAllText(foremanArtifactPath);
            Log.Debug("Parsing Foreman response from artifact: {Path}", foremanArtifactPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read Foreman artifact {Path}", foremanArtifactPath);
            response.Reason = $"Failed to read Foreman artifact: {ex.Message}";
            return response;
        }

        try
        {
            var doc = JsonDocument.Parse(jsonSource);
            response.FullResponse = doc;
            var root = doc.RootElement;

            var outcomeStr = root.TryGetProperty("outcome", out var outcomeEl)
                ? outcomeEl.GetString()?.ToUpperInvariant()
                : null;

            response.Outcome = outcomeStr switch
            {
                "RETRY" => AgentOutcome.Success,
                "ESCALATE" => AgentOutcome.Fail,
                _ => null
            };

            response.Reason = outcomeStr;

            if (root.TryGetProperty("reason", out var reasonEl))
                response.Notes = reasonEl.GetString();
            if (root.TryGetProperty("notes", out var notesEl))
                response.Notes = $"{response.Notes}\n{notesEl.GetString()}".Trim();
            if (root.TryGetProperty("inject_context", out var injectEl))
                response.InjectContext = injectEl.GetString();
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Foreman artifact is not valid JSON — auto-escalating");
            response.Reason = "Foreman artifact is not valid JSON";
        }

        return response;
    }
}
