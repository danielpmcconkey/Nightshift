using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Configuration;
using Nightshift.Engine.Models;
using Serilog;

namespace Nightshift.Engine.Agents;

public class AgentInvoker
{
    private readonly PromptBuilder _promptBuilder;
    private readonly OutcomeParser _outcomeParser;
    private static readonly ILogger Log = Serilog.Log.ForContext<AgentInvoker>();

    // Model tier map — loaded from appsettings or defaults
    private static readonly Dictionary<string, string> DefaultModelTierMap = new()
    {
        ["opus"] = "claude-opus-4-6",
        ["sonnet"] = "claude-sonnet-4-6",
        ["haiku"] = "claude-haiku-4-5-20251001"
    };

    private readonly Dictionary<string, string> _modelTierMap;

    // Env var whitelist for child processes
    private static readonly string[] DefaultEnvWhitelist =
        ["PATH", "HOME", "GIT_SSH_COMMAND", "ANTHROPIC_API_KEY"];

    private readonly string[] _envWhitelist;

    public AgentInvoker(PromptBuilder promptBuilder, OutcomeParser outcomeParser,
        IConfiguration? configuration = null)
    {
        _promptBuilder = promptBuilder;
        _outcomeParser = outcomeParser;

        // Try loading from config, fall back to defaults
        _modelTierMap = new Dictionary<string, string>(DefaultModelTierMap);
        var configSection = configuration?.GetSection("ModelTierMap");
        if (configSection?.GetChildren().Any() == true)
        {
            foreach (var child in configSection.GetChildren())
            {
                if (child.Value is not null)
                    _modelTierMap[child.Key] = child.Value;
            }
        }

        // Check env var overrides
        foreach (var tier in new[] { "opus", "sonnet", "haiku" })
        {
            var envKey = $"NIGHTSHIFT_MODEL_{tier.ToUpperInvariant()}";
            var envVal = Environment.GetEnvironmentVariable(envKey);
            if (!string.IsNullOrEmpty(envVal))
                _modelTierMap[tier] = envVal;
        }

        _envWhitelist = DefaultEnvWhitelist;
    }

    public string ResolveModel(string modelTier) =>
        _modelTierMap.TryGetValue(modelTier, out var model)
            ? model
            : throw new ArgumentException($"Unknown model tier: {modelTier}");

    public async Task<AgentResponse> Invoke(Card card, Project project, WorkflowStep step,
        string blueprintBasePath, string basePath, string? injectContext, CancellationToken ct)
    {
        var modelId = ResolveModel(step.ModelTier);
        var blueprint = _promptBuilder.LoadBlueprint(blueprintBasePath, step.BlueprintName);
        var addendum = _promptBuilder.LoadAddendum(project.RepoPath, project.AddendaSubpath, step.BlueprintName);

        var systemPrompt = addendum is not null
            ? $"{blueprint}\n\n---\n\n# Project Addendum\n\n{addendum}"
            : blueprint;

        var prompt = await _promptBuilder.BuildAgentPrompt(card, project, step, basePath, injectContext, ct);

        Log.Information("Invoking agent: card={CardId} step={Step} blueprint={Blueprint} model={Model}",
            card.Id, step.StepName, step.BlueprintName, modelId);

        // Build whitelisted environment
        var env = new Dictionary<string, string?>();
        foreach (var key in _envWhitelist)
        {
            var val = Environment.GetEnvironmentVariable(key);
            if (val is not null)
                env[key] = val;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));

            var result = await Cli.Wrap("claude")
                .WithArguments(args =>
                {
                    args.Add("-p");
                    args.Add("--append-system-prompt").Add(systemPrompt);
                    args.Add("--output-format").Add("json");
                    args.Add("--model").Add(modelId);
                    args.Add("--dangerously-skip-permissions");
                    args.Add("--no-session-persistence");
                    args.Add(prompt);
                })
                .WithWorkingDirectory(project.RepoPath)
                .WithEnvironmentVariables(env)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cts.Token);

            Log.Information("Agent completed: card={CardId} step={Step} exitCode={ExitCode}",
                card.Id, step.StepName, result.ExitCode);

            if (!string.IsNullOrWhiteSpace(result.StandardError))
                Log.Debug("Agent stderr: {Stderr}", result.StandardError);

            return _outcomeParser.Parse(result.StandardOutput, result.ExitCode,
                basePath, card.Id, step.StepName);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout, not engine shutdown
            Log.Warning("Agent timed out after {Seconds}s: card={CardId} step={Step}",
                step.TimeoutSeconds, card.Id, step.StepName);
            return new AgentResponse
            {
                Outcome = AgentOutcome.Fail,
                Reason = $"Agent timed out after {step.TimeoutSeconds} seconds",
                ExitCode = -1
            };
        }
    }

    public async Task<AgentResponse> InvokeForeman(Card card, Project project,
        WorkflowStep failedStep, AgentResponse agentResponse,
        Dictionary<string, int> conditionalCounts, string blueprintBasePath,
        string basePath, CancellationToken ct)
    {
        var modelId = ResolveModel("opus");
        var blueprint = _promptBuilder.LoadBlueprint(blueprintBasePath, "foreman-jurisdiction");
        var addendum = _promptBuilder.LoadAddendum(project.RepoPath, project.AddendaSubpath, "foreman-jurisdiction");

        var systemPrompt = addendum is not null
            ? $"{blueprint}\n\n---\n\n# Project Addendum\n\n{addendum}"
            : blueprint;

        var prompt = await _promptBuilder.BuildForemanPrompt(
            card, project, failedStep, agentResponse, conditionalCounts, basePath, ct);

        Log.Information("Invoking Foreman: card={CardId} failedStep={Step}", card.Id, failedStep.StepName);

        var env = new Dictionary<string, string?>();
        foreach (var key in _envWhitelist)
        {
            var val = Environment.GetEnvironmentVariable(key);
            if (val is not null)
                env[key] = val;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(10));

            var result = await Cli.Wrap("claude")
                .WithArguments(args =>
                {
                    args.Add("-p");
                    args.Add("--append-system-prompt").Add(systemPrompt);
                    args.Add("--output-format").Add("json");
                    args.Add("--model").Add(modelId);
                    args.Add("--dangerously-skip-permissions");
                    args.Add("--no-session-persistence");
                    args.Add(prompt);
                })
                .WithWorkingDirectory(project.RepoPath)
                .WithEnvironmentVariables(env)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cts.Token);

            Log.Information("Foreman completed: card={CardId} exitCode={ExitCode}",
                card.Id, result.ExitCode);

            // Parse Foreman response — reuse outcome parser but expect RETRY/ESCALATE
            return ParseForemanResponse(result.StandardOutput, result.ExitCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warning("Foreman timed out: card={CardId}", card.Id);
            return new AgentResponse
            {
                Outcome = null, // Will trigger auto-escalate
                Reason = "Foreman timed out"
            };
        }
    }

    private AgentResponse ParseForemanResponse(string? stdout, int exitCode)
    {
        var response = new AgentResponse { RawOutput = stdout, ExitCode = exitCode };

        if (string.IsNullOrWhiteSpace(stdout))
        {
            Log.Warning("Foreman returned empty response — auto-escalating");
            return response; // null outcome → auto-escalate
        }

        try
        {
            var doc = JsonDocument.Parse(stdout);
            response.FullResponse = doc;
            var root = doc.RootElement;

            // Handle Claude CLI wrapper
            if (!root.TryGetProperty("outcome", out var outcomeEl))
            {
                if (root.TryGetProperty("result", out var resultEl) &&
                    resultEl.ValueKind == JsonValueKind.String)
                {
                    try
                    {
                        var innerDoc = JsonDocument.Parse(resultEl.GetString()!);
                        root = innerDoc.RootElement;
                        root.TryGetProperty("outcome", out outcomeEl);
                    }
                    catch { }
                }
            }

            var outcomeStr = outcomeEl.ValueKind != JsonValueKind.Undefined
                ? outcomeEl.GetString()?.ToUpperInvariant()
                : null;

            // Map RETRY/ESCALATE to our enum (RETRY → Success means "proceed with retry")
            response.Outcome = outcomeStr switch
            {
                "RETRY" => AgentOutcome.Success,  // Success signals "Foreman says retry"
                "ESCALATE" => AgentOutcome.Fail,   // Fail signals "Foreman says escalate"
                _ => null                           // Unknown → auto-escalate
            };

            // Store the raw outcome string for the caller
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
            Log.Warning(ex, "Foreman returned unparseable JSON — auto-escalating");
            response.Reason = "Foreman returned unparseable response";
        }

        return response;
    }
}
