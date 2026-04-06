using System.Text.Json;
using Nightshift.Engine.Models;
using Serilog;

namespace Nightshift.Engine.Agents;

public class OutcomeParser
{
    private static readonly ILogger Log = Serilog.Log.ForContext<OutcomeParser>();

    public AgentResponse Parse(string? stdout, int exitCode, string basePath, int cardId, string stepName)
    {
        var response = new AgentResponse
        {
            RawOutput = stdout,
            ExitCode = exitCode
        };

        // Try reading process artifact file first (agent may have written it)
        var artifactPath = Path.Combine(basePath, "artifacts", cardId.ToString(), "process", $"{stepName}.json");
        string? jsonSource = null;

        if (File.Exists(artifactPath))
        {
            try
            {
                jsonSource = File.ReadAllText(artifactPath);
                Log.Debug("Parsing outcome from artifact file: {Path}", artifactPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to read artifact file {Path}", artifactPath);
            }
        }

        // Fallback: try parsing stdout
        if (jsonSource is null && !string.IsNullOrWhiteSpace(stdout))
        {
            jsonSource = stdout;
            Log.Debug("Parsing outcome from stdout");
        }

        if (jsonSource is null)
        {
            Log.Warning("No parseable output from agent for card {CardId} step {Step}", cardId, stepName);
            response.Outcome = AgentOutcome.Fail;
            response.Reason = "No output from agent";
            return response;
        }

        try
        {
            // Claude CLI with --output-format json wraps the response —
            // the actual content may be in a "result" field
            var doc = JsonDocument.Parse(jsonSource);
            response.FullResponse = doc;

            var root = doc.RootElement;

            // Try to find outcome in the root or nested in "result"
            if (!root.TryGetProperty("outcome", out var outcomeElement))
            {
                if (root.TryGetProperty("result", out var resultElement) &&
                    resultElement.ValueKind == JsonValueKind.String)
                {
                    // Try parsing the result string as JSON
                    try
                    {
                        var innerDoc = JsonDocument.Parse(resultElement.GetString()!);
                        root = innerDoc.RootElement;
                        root.TryGetProperty("outcome", out outcomeElement);
                    }
                    catch
                    {
                        // result is not JSON
                    }
                }
            }

            if (outcomeElement.ValueKind != JsonValueKind.Undefined)
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
                Log.Warning("Could not parse outcome from agent response for card {CardId} step {Step}",
                    cardId, stepName);
                // null outcome → unknown → will route to Foreman
            }
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Failed to parse JSON from agent for card {CardId} step {Step}", cardId, stepName);
            response.Outcome = AgentOutcome.Fail;
            response.Reason = "Unparseable agent response";
        }

        return response;
    }
}
