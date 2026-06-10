using M351.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace M351.Infrastructure.Data;

/// <summary>
/// Carimba tenant_id em INSERTs e bloqueia gravação cruzada entre tenants (Princípio 4).
/// </summary>
public class TenantSaveChangesInterceptor(TenantContext tenantContext) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<ITenantEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.TenantId == Guid.Empty)
                    {
                        entry.Entity.TenantId = tenantContext.TenantId
                            ?? throw new InvalidOperationException(
                                "Tentativa de INSERT sem tenant: TenantId vazio e nenhum tenant no contexto da requisição.");
                    }
                    else if (tenantContext.TenantId is { } tenant && entry.Entity.TenantId != tenant)
                    {
                        throw new InvalidOperationException(
                            "Violação de isolamento: INSERT com tenant_id divergente do tenant autenticado.");
                    }

                    break;

                case EntityState.Modified:
                    var tenantProp = entry.Property(nameof(ITenantEntity.TenantId));
                    if (tenantProp.IsModified)
                    {
                        throw new InvalidOperationException("Violação de isolamento: tenant_id é imutável.");
                    }

                    if (tenantContext.TenantId is { } t && entry.Entity.TenantId != t)
                    {
                        throw new InvalidOperationException(
                            "Violação de isolamento: UPDATE em entidade de outro tenant.");
                    }

                    break;
            }
        }
    }
}
