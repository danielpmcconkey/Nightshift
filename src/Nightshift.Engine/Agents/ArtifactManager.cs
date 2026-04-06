using Serilog;

namespace Nightshift.Engine.Agents;

public class ArtifactManager
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ArtifactManager>();

    public string GetArtifactDir(string basePath, int cardId) =>
        Path.Combine(basePath, "artifacts", cardId.ToString());

    public string GetProcessDir(string basePath, int cardId) =>
        Path.Combine(GetArtifactDir(basePath, cardId), "process");

    public string GetProcessArtifactPath(string basePath, int cardId, string stepName) =>
        Path.Combine(GetProcessDir(basePath, cardId), $"{stepName}.json");

    public string GetScratchpadPath(string basePath, int cardId) =>
        Path.Combine(GetArtifactDir(basePath, cardId), "notes.md");

    public void EnsureDirectories(string basePath, int cardId)
    {
        Directory.CreateDirectory(GetProcessDir(basePath, cardId));
    }

    public async Task SaveProcessArtifact(string basePath, int cardId, string stepName,
        string content, CancellationToken ct)
    {
        EnsureDirectories(basePath, cardId);
        var path = GetProcessArtifactPath(basePath, cardId, stepName);
        await File.WriteAllTextAsync(path, content, ct);
        Log.Information("Saved process artifact: {Path}", path);
    }

    public async Task<string?> ReadProcessArtifact(string basePath, int cardId, string stepName,
        CancellationToken ct)
    {
        var path = GetProcessArtifactPath(basePath, cardId, stepName);
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, ct);
    }

    public async Task<string?> ReadScratchpad(string basePath, int cardId, CancellationToken ct)
    {
        var path = GetScratchpadPath(basePath, cardId);
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, ct);
    }

    public void CleanStaleArtifacts(string basePath, int cardId, IEnumerable<string> staleStepNames)
    {
        foreach (var stepName in staleStepNames)
        {
            var path = GetProcessArtifactPath(basePath, cardId, stepName);
            if (File.Exists(path))
            {
                File.Delete(path);
                Log.Information("Cleaned stale artifact: {Path}", path);
            }
        }
    }

    public List<string> GetDownstreamStepNames(
        IReadOnlyList<Models.WorkflowStep> steps, string currentStepName)
    {
        var currentSeq = steps.FirstOrDefault(s => s.StepName == currentStepName)?.Sequence ?? 0;
        return steps
            .Where(s => s.Sequence > currentSeq)
            .Select(s => s.StepName)
            .ToList();
    }
}
