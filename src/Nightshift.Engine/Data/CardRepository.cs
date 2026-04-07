using System.Text.Json;
using Nightshift.Engine.Models;
using Npgsql;
using NpgsqlTypes;
using Serilog;

namespace Nightshift.Engine.Data;

public class CardRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private static readonly ILogger Log = Serilog.Log.ForContext<CardRepository>();

    public CardRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<Card?> GetById(int cardId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, project_id, title, description, priority, status,
                   current_step, conditional_counts, created_at, updated_at, completed_at
            FROM nightshift.card WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", cardId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadCard(reader);
    }

    public async Task UpdateStatus(int cardId, string status, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE nightshift.card SET status = @status, updated_at = now()
            WHERE id = @id", tx.Connection!, tx);
        cmd.Parameters.AddWithValue("id", cardId);
        cmd.Parameters.AddWithValue("status", status);
        await cmd.ExecuteNonQueryAsync(ct);
        Log.Information("Card {CardId} status → {Status}", cardId, status);
    }

    public async Task AdvanceStep(int cardId, string nextStep, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE nightshift.card
            SET current_step = @step, status = 'in_progress', updated_at = now()
            WHERE id = @id", tx.Connection!, tx);
        cmd.Parameters.AddWithValue("id", cardId);
        cmd.Parameters.AddWithValue("step", nextStep);
        await cmd.ExecuteNonQueryAsync(ct);
        Log.Information("Card {CardId} advanced to step {Step}", cardId, nextStep);
    }

    public async Task MarkComplete(int cardId, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE nightshift.card
            SET status = 'complete', completed_at = now(), updated_at = now()
            WHERE id = @id", tx.Connection!, tx);
        cmd.Parameters.AddWithValue("id", cardId);
        await cmd.ExecuteNonQueryAsync(ct);
        Log.Information("Card {CardId} marked COMPLETE", cardId);
    }

    public async Task MarkBlocked(int cardId, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE nightshift.card
            SET status = 'blocked', updated_at = now()
            WHERE id = @id", tx.Connection!, tx);
        cmd.Parameters.AddWithValue("id", cardId);
        await cmd.ExecuteNonQueryAsync(ct);
        Log.Information("Card {CardId} marked BLOCKED", cardId);
    }

    public async Task UpdateConditionalCounts(int cardId, Dictionary<string, int> counts,
        NpgsqlTransaction tx, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(counts);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE nightshift.card SET conditional_counts = @counts::jsonb, updated_at = now()
            WHERE id = @id", tx.Connection!, tx);
        cmd.Parameters.AddWithValue("id", cardId);
        cmd.Parameters.AddWithValue("counts", json);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Dictionary<string, int>> GetConditionalCounts(int cardId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT conditional_counts FROM nightshift.card WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", cardId);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null || result is DBNull) return new Dictionary<string, int>();
        var json = result.ToString()!;
        return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? [];
    }

    public async Task AddDependency(int cardId, int dependsOnId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO nightshift.card_dependency (card_id, depends_on_id)
            VALUES (@cardId, @depId)
            ON CONFLICT DO NOTHING", conn);
        cmd.Parameters.AddWithValue("cardId", cardId);
        cmd.Parameters.AddWithValue("depId", dependsOnId);
        await cmd.ExecuteNonQueryAsync(ct);
        Log.Information("Card {CardId} now depends on card {DependsOnId}", cardId, dependsOnId);
    }

    public async Task RemoveDependency(int cardId, int dependsOnId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            DELETE FROM nightshift.card_dependency
            WHERE card_id = @cardId AND depends_on_id = @depId", conn);
        cmd.Parameters.AddWithValue("cardId", cardId);
        cmd.Parameters.AddWithValue("depId", dependsOnId);
        await cmd.ExecuteNonQueryAsync(ct);
        Log.Information("Removed dependency: card {CardId} no longer depends on card {DependsOnId}", cardId, dependsOnId);
    }

    public async Task<List<int>> GetDependencies(int cardId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT depends_on_id FROM nightshift.card_dependency WHERE card_id = @cardId", conn);
        cmd.Parameters.AddWithValue("cardId", cardId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var deps = new List<int>();
        while (await reader.ReadAsync(ct))
            deps.Add(reader.GetInt32(0));
        return deps;
    }

    public async Task<List<(int DependsOnId, string Status)>> GetDependencyStatuses(int cardId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT cd.depends_on_id, c.status
            FROM nightshift.card_dependency cd
            JOIN nightshift.card c ON c.id = cd.depends_on_id
            WHERE cd.card_id = @cardId", conn);
        cmd.Parameters.AddWithValue("cardId", cardId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<(int, string)>();
        while (await reader.ReadAsync(ct))
            result.Add((reader.GetInt32(0), reader.GetString(1)));
        return result;
    }

    private static Card ReadCard(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        ProjectId = reader.GetInt32(1),
        Title = reader.GetString(2),
        Description = reader.GetString(3),
        Priority = reader.GetInt32(4),
        Status = reader.GetString(5),
        CurrentStep = reader.IsDBNull(6) ? null : reader.GetString(6),
        ConditionalCounts = reader.IsDBNull(7) ? null : JsonDocument.Parse(reader.GetString(7)),
        CreatedAt = reader.GetDateTime(8),
        UpdatedAt = reader.GetDateTime(9),
        CompletedAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10)
    };
}
