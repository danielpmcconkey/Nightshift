using System.Text.Json;
using Nightshift.Engine.Models;
using Npgsql;
using Serilog;

namespace Nightshift.Engine.Data;

public class WorkflowRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private static readonly ILogger Log = Serilog.Log.ForContext<WorkflowRepository>();

    public WorkflowRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<Workflow?> GetForProject(Project project, CancellationToken ct)
    {
        var workflowId = project.WorkflowId;
        if (workflowId is null)
            return await GetDefault(ct);
        return await GetById(workflowId.Value, ct);
    }

    public async Task<Workflow?> GetDefault(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, name, is_default FROM nightshift.workflow
            WHERE is_default = true LIMIT 1", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var workflow = new Workflow
        {
            Id = reader.GetInt32(0),
            Name = reader.GetString(1),
            IsDefault = reader.GetBoolean(2)
        };
        await reader.CloseAsync();
        await conn.CloseAsync();

        workflow.Steps = await GetSteps(workflow.Id, ct);
        return workflow;
    }

    public async Task<Workflow?> GetById(int workflowId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, name, is_default FROM nightshift.workflow
            WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", workflowId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var workflow = new Workflow
        {
            Id = reader.GetInt32(0),
            Name = reader.GetString(1),
            IsDefault = reader.GetBoolean(2)
        };
        await reader.CloseAsync();
        await conn.CloseAsync();

        workflow.Steps = await GetSteps(workflow.Id, ct);
        return workflow;
    }

    public async Task<List<Workflow>> GetAll(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, name, is_default FROM nightshift.workflow ORDER BY name", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var workflows = new List<Workflow>();
        while (await reader.ReadAsync(ct))
        {
            workflows.Add(new Workflow
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                IsDefault = reader.GetBoolean(2)
            });
        }
        await reader.CloseAsync();
        await conn.CloseAsync();

        foreach (var wf in workflows)
            wf.Steps = await GetSteps(wf.Id, ct);

        return workflows;
    }

    private async Task<List<WorkflowStep>> GetSteps(int workflowId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, workflow_id, step_name, sequence, blueprint_name, model_tier,
                   timeout_seconds, allowed_outcomes, transition_map,
                   is_review_gate, response_step, rewind_target
            FROM nightshift.workflow_step
            WHERE workflow_id = @wfId
            ORDER BY sequence", conn);
        cmd.Parameters.AddWithValue("wfId", workflowId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var steps = new List<WorkflowStep>();
        while (await reader.ReadAsync(ct))
        {
            steps.Add(new WorkflowStep
            {
                Id = reader.GetInt32(0),
                WorkflowId = reader.GetInt32(1),
                StepName = reader.GetString(2),
                Sequence = reader.GetInt32(3),
                BlueprintName = reader.GetString(4),
                ModelTier = reader.GetString(5),
                TimeoutSeconds = reader.GetInt32(6),
                AllowedOutcomes = reader.GetFieldValue<string[]>(7),
                TransitionMap = JsonDocument.Parse(reader.GetString(8)),
                IsReviewGate = reader.GetBoolean(9),
                ResponseStep = reader.IsDBNull(10) ? null : reader.GetString(10),
                RewindTarget = reader.IsDBNull(11) ? null : reader.GetString(11)
            });
        }

        return steps;
    }
}
