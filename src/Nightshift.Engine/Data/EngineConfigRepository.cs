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

    public async Task<bool> IsEngineEnabled(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT engine_enabled FROM nightshift.engine_config WHERE id = 1", conn);
        var result = await cmd.ExecuteScalarAsync(ct);

        if (result is null || result is DBNull)
        {
            Log.Warning("Engine config row missing — treating as disabled (fail-safe)");
            return false;
        }

        return (bool)result;
    }
}
