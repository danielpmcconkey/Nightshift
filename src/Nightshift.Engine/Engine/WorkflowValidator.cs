using Nightshift.Engine.Models;
using Serilog;

namespace Nightshift.Engine.Engine;

public static class WorkflowValidator
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(WorkflowValidator));

    public static void Validate(Workflow workflow, string blueprintBasePath)
    {
        var errors = new List<string>();
        var stepsByName = workflow.Steps.ToDictionary(s => s.StepName);

        if (workflow.Steps.Count == 0)
        {
            errors.Add($"Workflow '{workflow.Name}' has no steps");
            ThrowIfErrors(workflow.Name, errors);
        }

        foreach (var step in workflow.Steps)
        {
            // Check blueprint exists on disk
            var blueprintPath = Path.Combine(blueprintBasePath, $"{step.BlueprintName}.md");
            if (!File.Exists(blueprintPath))
                errors.Add($"Step '{step.StepName}': blueprint not found at {blueprintPath}");

            // Check transition_map references
            if (step.TransitionMap is not null)
            {
                foreach (var prop in step.TransitionMap.RootElement.EnumerateObject())
                {
                    var target = prop.Value.GetString();
                    if (target != "COMPLETE" && target is not null && !stepsByName.ContainsKey(target))
                        errors.Add($"Step '{step.StepName}': transition_map references unknown step '{target}'");
                }
            }

            // Review gate consistency
            if (step.IsReviewGate)
            {
                if (string.IsNullOrEmpty(step.ResponseStep))
                    errors.Add($"Review gate '{step.StepName}': response_step is required");
                else if (!stepsByName.ContainsKey(step.ResponseStep))
                    errors.Add($"Review gate '{step.StepName}': response_step '{step.ResponseStep}' not found");

                if (string.IsNullOrEmpty(step.RewindTarget))
                    errors.Add($"Review gate '{step.StepName}': rewind_target is required");
                else if (!stepsByName.ContainsKey(step.RewindTarget))
                    errors.Add($"Review gate '{step.StepName}': rewind_target '{step.RewindTarget}' not found");
            }
            else
            {
                if (!string.IsNullOrEmpty(step.ResponseStep))
                    errors.Add($"Non-review-gate '{step.StepName}': response_step must be NULL");
                if (!string.IsNullOrEmpty(step.RewindTarget))
                    errors.Add($"Non-review-gate '{step.StepName}': rewind_target must be NULL");
            }
        }

        // Check reachability from first step
        var firstStep = workflow.Steps.OrderBy(s => s.Sequence).First();
        var reachable = new HashSet<string>();
        TraceReachability(firstStep.StepName, stepsByName, reachable);

        foreach (var step in workflow.Steps)
        {
            if (!reachable.Contains(step.StepName))
                errors.Add($"Step '{step.StepName}' is not reachable from the first step");
        }

        // Check terminal step exists
        var hasTerminal = workflow.Steps.Any(s =>
            s.TransitionMap is not null &&
            s.TransitionMap.RootElement.EnumerateObject()
                .Any(p => p.Value.GetString() == "COMPLETE"));
        if (!hasTerminal)
            errors.Add("Workflow has no terminal step (no transition to 'COMPLETE')");

        ThrowIfErrors(workflow.Name, errors);
        Log.Information("Workflow '{WorkflowName}' validated: {StepCount} steps, all checks passed",
            workflow.Name, workflow.Steps.Count);
    }

    private static void TraceReachability(string stepName, Dictionary<string, WorkflowStep> steps,
        HashSet<string> visited)
    {
        if (!visited.Add(stepName)) return;
        if (!steps.TryGetValue(stepName, out var step)) return;

        // Follow transition_map targets
        if (step.TransitionMap is not null)
        {
            foreach (var prop in step.TransitionMap.RootElement.EnumerateObject())
            {
                var target = prop.Value.GetString();
                if (target is not null && target != "COMPLETE")
                    TraceReachability(target, steps, visited);
            }
        }

        // Follow review gate links
        if (step.ResponseStep is not null) TraceReachability(step.ResponseStep, steps, visited);
        if (step.RewindTarget is not null) TraceReachability(step.RewindTarget, steps, visited);
    }

    private static void ThrowIfErrors(string workflowName, List<string> errors)
    {
        if (errors.Count == 0) return;
        var message = $"Workflow '{workflowName}' validation failed:\n" +
            string.Join("\n", errors.Select(e => $"  - {e}"));
        Log.Error(message);
        throw new InvalidOperationException(message);
    }
}
