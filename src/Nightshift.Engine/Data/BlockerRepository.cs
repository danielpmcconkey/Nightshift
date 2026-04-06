using System.Text.Json;
using Nightshift.Engine.Models;
using Npgsql;
using Serilog;

namespace Nightshift.Engine.Data;

public class BlockerRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private static readonly ILogger Log = Serilog.Log.ForContext<BlockerRepository>();

    public BlockerRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task Create(int cardId, string stepName, JsonDocument? agentResponse,
        JsonDocument? foremanAssessment, string? context, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO nightshift.blocker (card_id, step_name, agent_response, foreman_assessment, context)
            VALUES (@cardId, @step, @agentResponse::jsonb, @foremanAssessment::jsonb, @context)", tx.Connection!, tx);
        cmd.Parameters.AddWithValue("cardId", cardId);
        cmd.Parameters.AddWithValue("step", stepName);
        cmd.Parameters.AddWithValue("agentResponse",
            agentResponse is not null ? agentResponse.RootElement.GetRawText() : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("foremanAssessment",
            foremanAssessment is not null ? foremanAssessment.RootElement.GetRawText() : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("context", context ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        Log.Warning("Blocker created for card {CardId} at step {Step}", cardId, stepName);
    }
}
