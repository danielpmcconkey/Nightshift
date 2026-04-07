using Nightshift.Engine.Models;
using Npgsql;
using Serilog;

namespace Nightshift.Engine.Data;

public class TaskQueueRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private static readonly ILogger Log = Serilog.Log.ForContext<TaskQueueRepository>();

    public TaskQueueRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>
    /// Atomically claim the highest-priority pending task.
    /// Uses SELECT FOR UPDATE SKIP LOCKED in an explicit transaction.
    /// Returns (task, connection, transaction) — caller must commit/rollback.
    /// </summary>
    public async Task<(TaskQueueItem? Task, NpgsqlConnection Connection, NpgsqlTransaction Transaction)>
        ClaimNext(CancellationToken ct)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct);
        var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            await using var cmd = new NpgsqlCommand(@"
                UPDATE nightshift.task_queue
                SET status = 'claimed', claimed_at = now()
                WHERE id = (
                    SELECT tq.id
                    FROM nightshift.task_queue tq
                    JOIN nightshift.card c ON c.id = tq.card_id
                    WHERE tq.status = 'pending'
                      AND c.status IN ('queued', 'in_progress')
                      AND NOT EXISTS (
                          SELECT 1
                          FROM nightshift.card_dependency cd
                          JOIN nightshift.card dep ON dep.id = cd.depends_on_id
                          WHERE cd.card_id = c.id
                            AND dep.status != 'complete'
                      )
                    ORDER BY c.priority ASC, tq.created_at ASC
                    FOR UPDATE OF tq SKIP LOCKED
                    LIMIT 1
                )
                RETURNING id, card_id, step_name", conn, tx);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                await reader.CloseAsync();
                await tx.CommitAsync(ct);
                await conn.DisposeAsync();
                return (null, null!, null!);
            }

            var task = new TaskQueueItem
            {
                Id = reader.GetInt64(0),
                CardId = reader.GetInt32(1),
                StepName = reader.GetString(2),
                Status = "claimed",
                ClaimedAt = DateTime.UtcNow
            };
            await reader.CloseAsync();

            Log.Information("Claimed task {TaskId} for card {CardId} step {Step}",
                task.Id, task.CardId, task.StepName);

            // Return open connection+transaction — caller commits after processing
            return (task, conn, tx);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            await conn.DisposeAsync();
            throw;
        }
    }

    public async Task<long> Enqueue(int cardId, string stepName, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO nightshift.task_queue (card_id, step_name)
            VALUES (@cardId, @step)
            RETURNING id", tx.Connection!, tx);
        cmd.Parameters.AddWithValue("cardId", cardId);
        cmd.Parameters.AddWithValue("step", stepName);
        var newId = (long)(await cmd.ExecuteScalarAsync(ct))!;
        Log.Information("Enqueued task {TaskId} for card {CardId} step {Step}", newId, cardId, stepName);
        return newId;
    }

    public async Task Complete(long taskId, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE nightshift.task_queue
            SET status = 'complete', completed_at = now()
            WHERE id = @id", tx.Connection!, tx);
        cmd.Parameters.AddWithValue("id", taskId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task Fail(long taskId, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE nightshift.task_queue
            SET status = 'failed', completed_at = now()
            WHERE id = @id", tx.Connection!, tx);
        cmd.Parameters.AddWithValue("id", taskId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecoverOrphaned(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE nightshift.task_queue
            SET status = 'pending', claimed_at = NULL
            WHERE status = 'claimed'
            RETURNING id, card_id, step_name", conn);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var count = 0;
        while (await reader.ReadAsync(ct))
        {
            Log.Warning("Recovered orphaned task {TaskId} for card {CardId} step {Step}",
                reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2));
            count++;
        }

        if (count > 0)
            Log.Information("Recovered {Count} orphaned tasks", count);
    }
}
