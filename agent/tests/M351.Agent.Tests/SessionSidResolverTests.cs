using System.Security.Principal;
using MonitorAgentService;
using Xunit;

namespace M351.Agent.Tests;

// O caminho completo do SESSION_START (OnSessionChange/EmitSessionEvent) exige o SCM e uma
// sessão real; o que é testável sem SCM é a resolução de SID em si, no mesmo formato
// "DOMÍNIO\usuário" que o EmitSessionEvent monta a partir do WTSQuerySessionInformation.
public class SessionSidResolverTests
{
    [Fact]
    public void Usuario_atual_resolve_para_o_proprio_SID()
    {
        // Independente de idioma do Windows: compara com o SID do token do próprio processo.
        var expected = WindowsIdentity.GetCurrent().User!.Value;
        var account = $@"{Environment.UserDomainName}\{Environment.UserName}";
        Assert.Equal(expected, SessionSidResolver.TryResolve(account));
    }

    [Fact]
    public void Conta_inexistente_retorna_null_sem_lancar()
    {
        Assert.Null(SessionSidResolver.TryResolve(@"DOMINIO-INEXISTENTE\usuario-inexistente-9f3a"));
    }

    [Fact]
    public void Entrada_vazia_ou_null_retorna_null()
    {
        Assert.Null(SessionSidResolver.TryResolve(null));
        Assert.Null(SessionSidResolver.TryResolve(""));
        Assert.Null(SessionSidResolver.TryResolve("   "));
    }
}
