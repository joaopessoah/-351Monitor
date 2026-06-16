namespace M351.Agent.Core.Update;

/// <summary>O que o agente deve fazer com um manifesto (Secao 6.7).</summary>
public enum UpdateAction
{
    /// <summary>Nada a fazer: ja na versao mais nova (ou mais nova), e &gt;= min_version.</summary>
    None,

    /// <summary>version &gt; current e current &gt;= min_version: atualizar respeitando a cadencia normal.</summary>
    Update,

    /// <summary>current &lt; min_version: atualizar FORCADO imediatamente (independente de janela).</summary>
    ForcedUpdate
}

public readonly record struct UpdateDecision(UpdateAction Action, string Reason)
{
    public bool ShouldUpdate => Action is UpdateAction.Update or UpdateAction.ForcedUpdate;
}

/// <summary>
/// Decisao pura (sem IO) de update a partir do manifesto e da versao corrente. Regras (Secao 6.7):
///   - manifesto null (204/erro)                -> None.
///   - semver(version) ou semver(min_version) inparseaveis, ou current inparseavel -> None (seguro).
///   - current &lt; min_version                     -> ForcedUpdate (mesmo que version &lt;= current; o
///                                                   target do download e sempre manifest.version).
///   - version &gt; current                         -> Update.
///   - version &lt;= current e current &gt;= min_version -> None.
/// </summary>
public static class UpdatePlanner
{
    public static UpdateDecision Decide(UpdateManifest? manifest, string currentVersion)
    {
        if (manifest is null)
            return new UpdateDecision(UpdateAction.None, "sem release publicado (204)");

        if (!SemVer.TryParse(currentVersion, out var current))
            return new UpdateDecision(UpdateAction.None, $"versao corrente inparseavel: '{currentVersion}'");

        if (!SemVer.TryParse(manifest.Version, out var target))
            return new UpdateDecision(UpdateAction.None, $"version do manifesto inparseavel: '{manifest.Version}'");

        // min_version e opcional no contrato; ausente/invalido => sem piso (nao forca).
        var hasMin = SemVer.TryParse(manifest.MinVersion, out var min);

        if (hasMin && current < min)
            return new UpdateDecision(UpdateAction.ForcedUpdate,
                $"current {current} < min_version {min}: update forcado para {target}");

        if (target > current)
            return new UpdateDecision(UpdateAction.Update, $"version {target} > current {current}");

        return new UpdateDecision(UpdateAction.None, $"version {target} <= current {current}");
    }
}
