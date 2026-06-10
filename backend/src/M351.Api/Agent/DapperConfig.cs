using System.Data;
using Dapper;

namespace M351.Api.Agent;

/// <summary>
/// Configuração global do Dapper (hot paths da ingestão — Seção 7):
/// snake_case → PascalCase e leitura/escrita de timestamptz como DateTimeOffset UTC
/// (o Npgsql devolve DateTime Kind=Utc e só aceita DateTimeOffset com offset 0).
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
