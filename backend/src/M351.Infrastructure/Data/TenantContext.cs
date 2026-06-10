namespace M351.Infrastructure.Data;

/// <summary>
/// Tenant corrente da requisição (scoped). Preenchido a partir do claim do JWT pelo middleware da API.
/// Quando nulo (fluxos anônimos: login, refresh, aceite de convite, backoffice), o filtro global
/// retorna vazio e o acesso a dados exige IgnoreQueryFilters explícito.
/// </summary>
public class TenantContext
{
    public Guid? TenantId { get; set; }
}
