using Nightshift.Engine.Models;
using Npgsql;
using Serilog;

namespace Nightshift.Engine.Data;

public class ProjectRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private static readonly ILogger Log = Serilog.Log.ForContext<ProjectRepository>();

    public ProjectRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<Project?> GetById(int projectId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, name, repo_path, workflow_id, addenda_subpath, created_at
            FROM nightshift.project WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", projectId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new Project
        {
            Id = reader.GetInt32(0),
            Name = reader.GetString(1),
            RepoPath = reader.GetString(2),
            WorkflowId = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            AddendaSubpath = reader.GetString(4),
            CreatedAt = reader.GetDateTime(5)
        };
    }

    public async Task<List<Project>> GetAll(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, name, repo_path, workflow_id, addenda_subpath, created_at
            FROM nightshift.project ORDER BY name", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var projects = new List<Project>();
        while (await reader.ReadAsync(ct))
        {
            projects.Add(new Project
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                RepoPath = reader.GetString(2),
                WorkflowId = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                AddendaSubpath = reader.GetString(4),
                CreatedAt = reader.GetDateTime(5)
            });
        }

        return projects;
    }

    public void ValidateRepoPath(string repoPath)
    {
        if (!Path.IsPathRooted(repoPath))
            throw new InvalidOperationException($"repo_path must be absolute: {repoPath}");
        if (!repoPath.StartsWith("/workspace/"))
            throw new InvalidOperationException($"repo_path must be under /workspace/: {repoPath}");
        if (repoPath.Contains(".."))
            throw new InvalidOperationException($"repo_path must not contain '..': {repoPath}");
    }
}
