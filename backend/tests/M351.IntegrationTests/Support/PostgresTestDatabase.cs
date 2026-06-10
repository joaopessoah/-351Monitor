using Npgsql;

namespace M351.IntegrationTests.Support;

/// <summary>
/// Banco descartável por execução: cria m351_test_{sufixo aleatório} no Postgres apontado por
/// M351_TEST_PG (default localhost:5432, postgres/postgres) e o dropa no Dispose.
/// ADAPTAÇÃO DECLARADA (Seção 11.1): Postgres local/CI service no lugar de Testcontainers
/// (não há Docker nesta máquina).
/// </summary>
public sealed class PostgresTestDatabase : IDisposable
{
    public const string DefaultConnection = "Host=localhost;Port=5432;Username=postgres;Password=postgres";

    private readonly string _adminConnectionString;
    private readonly string _databaseName;

    public string ConnectionString { get; }

    public PostgresTestDatabase()
    {
        var baseConnection = Environment.GetEnvironmentVariable("M351_TEST_PG") ?? DefaultConnection;
        _databaseName = $"m351_test_{Guid.NewGuid():N}"[..30];

        var builder = new NpgsqlConnectionStringBuilder(baseConnection) { Database = "postgres" };
        _adminConnectionString = builder.ConnectionString;

        using (var connection = new NpgsqlConnection(_adminConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
            command.ExecuteNonQuery();
        }

        builder.Database = _databaseName;
        ConnectionString = builder.ConnectionString;
    }

    public void Dispose()
    {
        NpgsqlConnection.ClearAllPools();

        using var connection = new NpgsqlConnection(_adminConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
        command.ExecuteNonQuery();
    }
}
