using System.Data;
using System.Net;
using Dapper;

namespace M351.Api.Agent;

/// <summary>
/// Configuração global do Dapper (hot paths da ingestão — Seção 7):
/// snake_case → PascalCase e leitura/escrita de timestamptz como DateTimeOffset UTC
/// (o Npgsql devolve DateTime Kind=Utc e só aceita DateTimeOffset com offset 0).
/// F4.7: handler de IPAddress ↔ inet para o actor_ip da trilha gravada via Dapper
/// (AuditWriter.AddInTransactionAsync) — sem ele o Dapper recusa o parâmetro IPAddress.
/// </summary>
public static class DapperConfig
{
    private static bool _applied;

    public static void Apply()
    {
        if (_applied)
        {
            return;
        }

        _applied = true;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new IpAddressHandler());
    }

    /// <summary>
    /// inet ↔ IPAddress: o Npgsql infere o tipo inet quando o parâmetro recebe um IPAddress
    /// direto (Value), e devolve IPAddress na leitura. Sem handler o Dapper lança
    /// "IPAddress cannot be used as a parameter value".
    /// </summary>
    private sealed class IpAddressHandler : SqlMapper.TypeHandler<IPAddress>
    {
        public override IPAddress Parse(object value) => value switch
        {
            IPAddress ip => ip,
            string s => IPAddress.Parse(s),
            _ => throw new InvalidCastException($"Não é possível converter {value.GetType()} em IPAddress."),
        };

        public override void SetValue(IDbDataParameter parameter, IPAddress? value)
        {
            // null vira DBNull (ações de sistema/CLI sem IP); IPAddress vai direto (Npgsql → inet).
            parameter.Value = (object?)value ?? DBNull.Value;
        }
    }

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            DateTimeOffset dto => dto.ToUniversalTime(),
            _ => throw new InvalidCastException($"Não é possível converter {value.GetType()} em DateTimeOffset."),
        };

        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.Value = value.ToUniversalTime();
        }
    }
}
