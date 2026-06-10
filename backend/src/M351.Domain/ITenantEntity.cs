namespace M351.Domain;

/// <summary>
/// Toda entidade de dados pertence a um tenant (Princípio 4 — multi-tenant desde a primeira migration).
/// O filtro global de query e o interceptor de SaveChanges operam sobre esta interface.
/// </summary>
public interface ITenantEntity
{
    Guid TenantId { get; set; }
}
