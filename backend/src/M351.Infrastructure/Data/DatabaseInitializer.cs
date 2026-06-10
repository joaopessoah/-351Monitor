using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace M351.Infrastructure.Data;

public static class DatabaseInitializer
{
    /// <summary>
    /// Aplica as migrations e recarrega o catálogo de tipos do Npgsql — a migration inicial cria
    /// a extensão citext e o cache de tipos do pool (carregado antes) não a conhece ainda.
    /// </summary>
    public static async Task MigrateAsync(M351DbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var wasClosed = connection.State == ConnectionState.Closed;
        if (wasClosed)
        {
            await connection.OpenAsync(ct);
        }

        await connection.ReloadTypesAsync();

        if (wasClosed)
        {
            await connection.CloseAsync();
        }
    }
}
