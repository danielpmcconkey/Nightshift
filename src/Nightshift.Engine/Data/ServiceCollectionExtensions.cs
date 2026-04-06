using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Nightshift.Engine.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNightshiftDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Database");
        var host = section["Host"] ?? throw new InvalidOperationException("Database:Host is required");
        var port = section.GetValue<int>("Port", 5432);
        var database = section["Database"] ?? throw new InvalidOperationException("Database:Database is required");
        var username = section["Username"] ?? throw new InvalidOperationException("Database:Username is required");

        var password = Environment.GetEnvironmentVariable("NIGHTSHIFT_DB_PASSWORD")
            ?? throw new InvalidOperationException("NIGHTSHIFT_DB_PASSWORD environment variable is required");

        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
            Password = password
        }.ConnectionString;

        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        services.AddSingleton(dataSource);

        return services;
    }
}
