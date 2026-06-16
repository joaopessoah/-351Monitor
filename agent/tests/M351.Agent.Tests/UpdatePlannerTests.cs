using M351.Agent.Core.Update;
using Xunit;

namespace M351.Agent.Tests;

public class UpdatePlannerTests
{
    private static UpdateManifest Manifest(string version, string min = "1.0.0", string url = "https://srv/api/v1/agent/releases/MonitorAgent.msi")
        => new() { Version = version, MinVersion = min, Url = url, Sha256 = new string('a', 64) };

    [Fact]
    public void Version_maior_que_current_atualiza()
    {
        var d = UpdatePlanner.Decide(Manifest("1.1.0", min: "1.0.0"), "1.0.3");
        Assert.Equal(UpdateAction.Update, d.Action);
        Assert.True(d.ShouldUpdate);
    }

    [Fact]
    public void Current_abaixo_de_min_version_forca_update()
    {
        // current 1.0.0 < min 1.2.0 -> forcado, mesmo com version disponivel 1.3.0
        var d = UpdatePlanner.Decide(Manifest("1.3.0", min: "1.2.0"), "1.0.0");
        Assert.Equal(UpdateAction.ForcedUpdate, d.Action);
        Assert.True(d.ShouldUpdate);
    }

    [Fact]
    public void Forcado_tem_precedencia_mesmo_quando_version_igual_ou_menor_que_current()
    {
        // Caso de borda: o piso subiu acima da version anunciada e do current.
        // current < min vence: o target do download e a version do manifesto.
        var d = UpdatePlanner.Decide(Manifest("1.0.5", min: "1.0.4"), "1.0.3");
        Assert.Equal(UpdateAction.ForcedUpdate, d.Action);
    }

    [Fact]
    public void Manifesto_null_204_nada_a_fazer()
    {
        var d = UpdatePlanner.Decide(null, "1.0.0");
        Assert.Equal(UpdateAction.None, d.Action);
        Assert.False(d.ShouldUpdate);
    }

    [Fact]
    public void Version_igual_ao_current_nada_a_fazer()
    {
        var d = UpdatePlanner.Decide(Manifest("1.0.0", min: "1.0.0"), "1.0.0");
        Assert.Equal(UpdateAction.None, d.Action);
    }

    [Fact]
    public void Version_menor_que_current_e_acima_de_min_nada_a_fazer()
    {
        var d = UpdatePlanner.Decide(Manifest("1.0.0", min: "1.0.0"), "1.2.0");
        Assert.Equal(UpdateAction.None, d.Action);
    }

    [Fact]
    public void Decimal_dez_maior_que_nove_atualiza()
    {
        // protege contra comparacao lexicografica: "1.10.0" > "1.9.0"
        var d = UpdatePlanner.Decide(Manifest("1.10.0", min: "1.0.0"), "1.9.0");
        Assert.Equal(UpdateAction.Update, d.Action);
    }

    [Fact]
    public void Version_do_manifesto_invalida_nao_atualiza()
    {
        var d = UpdatePlanner.Decide(Manifest("nao-semver", min: "1.0.0"), "1.0.0");
        Assert.Equal(UpdateAction.None, d.Action);
    }

    [Fact]
    public void Min_version_ausente_nao_forca()
    {
        var d = UpdatePlanner.Decide(Manifest("1.0.0", min: ""), "1.0.0");
        Assert.Equal(UpdateAction.None, d.Action);
    }
}
