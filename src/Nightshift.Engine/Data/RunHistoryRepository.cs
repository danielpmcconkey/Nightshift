using Npgsql;
using Serilog;

namespace Nightshift.Engine.Data;

public class RunHistoryRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private static readonly ILogger Log = Serilog.Log.ForContext<RunHistoryRepository>();

    public RunHistoryRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<long> LogStart(int cardId, string stepName, string model,
        NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO nightshift.run_history (card_id, step_name, model)
            VALUES (@cardId, @step, @model)
            RETURNING id", tx.Connection!, tx);
        cmd.Parameters.AddWithValue("cardId", cardId);
        cmd.Parameters.AddWithValue("step", stepName);
        cmd.Parameters.AddWithValue("model", model);
        var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        Log.Information("Run history started: {RunId} card={CardId} step={Step} model={Model}",
            id, cardId, stepName, model);
        return id;
    }

    public async Task LogCompletion(long runId, string? outcome, string? notes,
        NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE nightshift.run_history
            SET completed_at = now(), outcome = @outcome, notes = @notes
            WHERE id = @id", tx.Connection!, tx);
        cmd.Parameters.AddWithValue("id", runId);
        cmd.Parameters.AddWithValue("outcome", outcome ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("notes", notes ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CleanupOldEntries(int retentionDays, int batchSize, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var totalDeleted = 0;

        while (!ct.IsCancellationRequested)
        {
            await using var cmd = new NpgsqlCommand(@"
                DELETE FROM nightshift.run_history
                WHERE id IN (
                    SELECT rh.id FROM nightshift.run_history rh
                    JOIN nightshift.card c ON c.id = rh.card_id
                    WHERE c.completed_at < now() - make_interval(days => @days)
                    LIMIT @batch
                )", conn);
            cmd.Parameters.AddWithValue("days", retentionDays);
            cmd.Parameters.AddWithValue("batch", batchSize);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            totalDeleted += deleted;

            if (deleted < batchSize) break;
        }

        if (totalDeleted > 0)
            Log.Information("Cleaned up {Count} old run history entries", totalDeleted);
    }
}
