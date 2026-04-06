using Npgsql;
using Serilog;

namespace Nightshift.Engine.Data;

public class EngineConfigRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private static readonly ILogger Log = Serilog.Log.ForContext<EngineConfigRepository>();

    public EngineConfigRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<bool> IsClutchEngaged(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT clutch_engaged FROM nightshift.engine_config WHERE id = 1", conn);
        var result = await cmd.ExecuteScalarAsync(ct);

        // Fail-safe: if row is missing, treat as disengaged
        if (result is null || result is DBNull)
        {
            Log.Warning("Engine config row missing — treating clutch as disengaged (fail-safe)");
            return false;
        }

        return (bool)result;
    }
}
