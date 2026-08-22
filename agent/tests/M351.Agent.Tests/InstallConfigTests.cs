using M351.Agent.Core.Logging;
using MonitorAgentService;
using Xunit;

namespace M351.Agent.Tests;

/// <summary>
/// install.json (Secao 6.6) — o canal do instalador. Aqui interessa sobretudo o DEFAULT das flags
/// de seguranca: verify_authenticode nasce FALSE (o certificado de code signing ainda nao existe),
/// e um install.json antigo, sem os campos novos, tem de continuar legivel.
/// </summary>
public class InstallConfigTests
{
    private static string NewDataDir() =>
        Path.Combine(Path.GetTempPath(), $"m351-install-{Guid.NewGuid():N}");

    [Fact]
    public void Verify_authenticode_nasce_desligado()
    {
        var config = new InstallConfig();
        Assert.False(config.VerifyAuthenticode);
        Assert.Null(config.ExpectedSignerCn);
    }

    [Fact]
    public void Round_trip_preserva_as_flags_de_assinatura()
    {
        var dataDir = NewDataDir();
        try
        {
            Directory.CreateDirectory(dataDir); // no MSI o InstallConfigCommand cria o diretorio
            new InstallConfig
            {
                ServerUrl = "https://api.exemplo.com.br",
                VerifyAuthenticode = true,
                ExpectedSignerCn = "Empresa Exemplo LTDA"
            }.Save(dataDir, new NullLogSink());

            var loaded = InstallConfig.TryLoad(dataDir, new NullLogSink());

            Assert.NotNull(loaded);
            Assert.True(loaded!.VerifyAuthenticode);
            Assert.Equal("Empresa Exemplo LTDA", loaded.ExpectedSignerCn);
            Assert.Equal("https://api.exemplo.com.br", loaded.ServerUrl);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch (Exception) { /* best-effort */ }
        }
    }

    [Fact]
    public void Install_json_antigo_sem_os_campos_novos_continua_legivel_com_default_seguro()
    {
        var dataDir = NewDataDir();
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(InstallConfig.PathFor(dataDir),
                """{"server_url":"https://api.exemplo.com.br","proxy_url":null,"verbose_debug":false}""");

            var loaded = InstallConfig.TryLoad(dataDir, new NullLogSink());

            Assert.NotNull(loaded);
            Assert.False(loaded!.VerifyAuthenticode); // ausente = desligado, nunca "ligado por acidente"
            Assert.Null(loaded.ExpectedSignerCn);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch (Exception) { /* best-effort */ }
        }
    }
}
