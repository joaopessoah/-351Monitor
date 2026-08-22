using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Exports;
using M351.IntegrationTests.Support;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// Relatório "Dados sobre mim" no pacote DSR de TITULAR (F5). Prova que o pacote deixa de ser só
/// CSV para máquina e passa a levar um documento que a pessoa consegue LER:
///  - entry dados-sobre-mim.html no ZIP de dsr_subject, com identificação, contagens iguais às do
///    manifest, política de mascaramento VIGENTE do tenant e os prazos fixos de retenção;
///  - EXTRATO DE ACESSOS: as consultas identificadas ao titular (view_report com device_user_id no
///    detail) com o NOME de quem consultou e SEM o IP do ator, mais a nota do recorte temporal;
///  - hash SHA-256 do HTML no manifest.json como recibo (confere byte a byte);
///  - pacote de DISPOSITIVO não leva o relatório (é documento sobre UMA pessoa).
/// </summary>
[Collection(ApiCollection.Name)]
public class DsrAboutMeReportTests(ApiTestFixture fixture)
{
    private string Conn => fixture.Database.ConnectionString;

    private async Task DrainExportsAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(Conn);
        var service = new ExportService(dataSource, fixture.ExportsDirectory);
        while (await service.RunOnceAsync() > 0) { }
    }

    private async Task<(Guid Id, string Sid, string WindowsUsername)> SeedDeviceUserAsync(
        Guid tenantId, Guid deviceId, string windowsUsername, string? displayName)
    {
        var id = Uuid7.NewUuid7();
        var sid = $"S-1-5-21-ABOUT-{Guid.NewGuid():N}"[..40];
        await TestDb.ExecuteAsync(Conn, """
            INSERT INTO device_users (
                id, tenant_id, device_id, windows_sid, windows_username, display_name,
                first_seen_at, last_seen_at)
            VALUES (@id, @t, @d, @sid, @wu, @dn, now() - interval '20 days', now())
            """,
            ("id", id), ("t", tenantId), ("d", deviceId), ("sid", sid),
            ("wu", windowsUsername), ("dn", displayName));
        return (id, sid, windowsUsername);
    }

    private async Task SeedIntervalAsync(Guid tenantId, Guid deviceId, Guid deviceUserId, string title)
    {
        var start = DateTimeOffset.UtcNow;
        await TestDb.ExecuteAsync(Conn, """
            INSERT INTO activity_intervals (
                id, tenant_id, device_id, device_user_id, started_at, ended_at, state, window_title, source_day)
            VALUES (@id, @t, @d, @u, @s, @e, 'active', @title, @day::date)
            """,
            ("id", Uuid7.NewUuid7()), ("t", tenantId), ("d", deviceId), ("u", deviceUserId),
            ("s", start.UtcDateTime), ("e", start.AddMinutes(5).UtcDateTime), ("title", title),
            ("day", start.UtcDateTime.ToString("yyyy-MM-dd")));
    }

    private static async Task<Dictionary<string, byte[]>> ReadZipAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entries = new Dictionary<string, byte[]>();
        foreach (var entry in archive.Entries)
        {
            await using var stream = entry.Open();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            entries[entry.FullName] = ms.ToArray();
        }

        return entries;
    }

    private static string TextOf(byte[] raw) =>
        raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF
            ? Encoding.UTF8.GetString(raw, 3, raw.Length - 3)
            : Encoding.UTF8.GetString(raw);

    // ============================================================ pacote de TITULAR
    [Fact]
    public async Task PacoteDeTitular_LevaRelatorioLegivel_ComExtratoDeAcessos_EHashNoManifest()
    {
        var org = await fixture.CreateOrganizationAsync($"AboutMe {Guid.NewGuid():N}"[..20]);
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var ownerToken = await AuthClient.LoginAsync(client, owner);

        // política de títulos EXPLÍCITA do tenant (o relatório deve refletir a vigente, e nunca
        // vazar o conteúdo cru dos padrões de mascaramento)
        await TestDb.ExecuteAsync(Conn, """
            INSERT INTO tenant_agent_configs (tenant_id, window_title_policy, masked_patterns, ignored_processes)
            VALUES (@t, 'APP_ONLY', @mp, @ip)
            ON CONFLICT (tenant_id) DO UPDATE SET window_title_policy = excluded.window_title_policy
            """,
            ("t", org.Id), ("mp", new[] { "(?i)PADRAO-SECRETO-ABC" }), ("ip", new[] { "keepass.exe" }));

        var device = await fixture.CreateDeviceAsync(org.Id, "NB-ABOUT-ME");
        var marta = await SeedDeviceUserAsync(org.Id, device.Id, "acme\\marta.reis", "Marta Reis");
        var outro = await SeedDeviceUserAsync(org.Id, device.Id, "acme\\outro.colega", "Outro Colega");
        await SeedIntervalAsync(org.Id, device.Id, marta.Id, "Planilha da Marta");

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        // ---- um acesso IDENTIFICADO aos dados da Marta (view_report com device_user_id)
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get,
            $"/api/v1/dashboard/summary?from={today}&to={today}&device_user_id={marta.Id}", ownerToken))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        }

        // ---- e um acesso aos dados do OUTRO titular: JAMAIS pode entrar no extrato da Marta
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get,
            $"/api/v1/dashboard/summary?from={today}&to={today}&device_user_id={outro.Id}", ownerToken))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        }

        // ---- e um acesso por DISPOSITIVO: não identifica titular, fica fora do extrato
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get,
            $"/api/v1/dashboard/summary?from={today}&to={today}&device_id={device.Id}", ownerToken))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        }

        // ---- pacote DSR do titular
        Guid jobId;
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Post,
            $"/api/v1/privacy/subjects/{marta.Id}/export", ownerToken))
        {
            var post = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
            using var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
            jobId = body.RootElement.GetProperty("id").GetGuid();
        }

        await DrainExportsAsync();

        Dictionary<string, byte[]> entries;
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Get,
            $"/api/v1/exports/{jobId}/download", ownerToken))
        {
            var download = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            entries = await ReadZipAsync(download);
        }

        Assert.True(entries.ContainsKey("dados-sobre-mim.html"));
        var html = TextOf(entries["dados-sobre-mim.html"]);

        // identificação do titular
        Assert.Contains("Marta Reis", html);
        Assert.Contains("acme\\marta.reis", html);
        Assert.Contains("NB-ABOUT-ME", html);

        // política de mascaramento VIGENTE (APP_ONLY), descrita — nunca o padrão cru
        Assert.Contains("Apenas o nome do aplicativo em foco", html);
        Assert.DoesNotContain("PADRAO-SECRETO-ABC", html);
        Assert.DoesNotContain("keepass", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("masked_patterns", html, StringComparison.OrdinalIgnoreCase);

        // prazos fixos de retenção
        Assert.Contains("90 dias", html);
        Assert.Contains("12 meses", html);
        Assert.Contains("24 meses", html);

        // extrato de acessos: quem consultou (nome do usuário do portal) e a nota do recorte
        Assert.Contains("Quem consultou meus dados", html);
        Assert.Contains("Usuário owner", html); // display_name do ator (ApiTestFixture)
        Assert.Contains("este extrato cobre acessos registrados a partir de",
            html.ToLowerInvariant());
        Assert.Contains("não identificam um titular", html);

        // o IP do ator JAMAIS é exposto ao titular
        var actorIp = await TestDb.ScalarAsync<string>(Conn,
            "SELECT host(actor_ip) FROM audit_log WHERE tenant_id = @t AND actor_ip IS NOT NULL LIMIT 1",
            ("t", org.Id));
        if (actorIp is not null)
        {
            Assert.DoesNotContain(actorIp, html);
        }
        Assert.DoesNotContain("actor_ip", html, StringComparison.OrdinalIgnoreCase);

        // nada do OUTRO titular no relatório da Marta
        Assert.DoesNotContain("Outro Colega", html);
        Assert.DoesNotContain("acme\\outro.colega", html);

        // rodapé de revisão jurídica
        Assert.Contains("sujeito a revisão jurídica", html);

        // ---- recibo no manifest: SHA-256 do HTML exatamente como gravado
        using var manifest = JsonDocument.Parse(TextOf(entries["manifest.json"]));
        var receipt = manifest.RootElement.GetProperty("receipt");
        Assert.Equal("dados-sobre-mim.html", receipt.GetProperty("file").GetString());
        var expectedHash = Convert.ToHexString(SHA256.HashData(entries["dados-sobre-mim.html"])).ToLowerInvariant();
        Assert.Equal(expectedHash, receipt.GetProperty("sha256").GetString());

        // uma única consulta identificada à Marta (as outras duas não são dela)
        Assert.Equal(1, receipt.GetProperty("access_statement_rows").GetInt32());

        // as contagens do relatório saem do MESMO apuramento do manifest
        Assert.Equal(1, manifest.RootElement.GetProperty("counts").GetProperty("activity_intervals").GetInt32());
    }

    // ============================================================ pacote de DISPOSITIVO
    [Fact]
    public async Task PacoteDeDispositivo_NaoLevaRelatorioSobreMim()
    {
        var org = await fixture.CreateOrganizationAsync($"AboutDev {Guid.NewGuid():N}"[..20]);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var adminToken = await AuthClient.LoginAsync(client, admin);

        var device = await fixture.CreateDeviceAsync(org.Id, "NB-ABOUT-DEV");
        var ana = await SeedDeviceUserAsync(org.Id, device.Id, "acme\\ana.dev", "Ana Dev");
        var bob = await SeedDeviceUserAsync(org.Id, device.Id, "acme\\bob.dev", "Bob Dev");
        await SeedIntervalAsync(org.Id, device.Id, ana.Id, "Janela Ana");
        await SeedIntervalAsync(org.Id, device.Id, bob.Id, "Janela Bob");

        Guid jobId;
        using (var request = AuthClient.AuthorizedRequest(HttpMethod.Post,
            $"/api/v1/privacy/devices/{device.Id}/export", adminToken))
        {
            var post = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
            using var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
            jobId = body.RootElement.GetProperty("id").GetGuid();
        }

        await DrainExportsAsync();

        using var download = AuthClient.AuthorizedRequest(HttpMethod.Get,
            $"/api/v1/exports/{jobId}/download", adminToken);
        var response = await client.SendAsync(download);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entries = await ReadZipAsync(response);
        // documento é sobre UMA pessoa: um pacote com vários titulares não o leva
        Assert.False(entries.ContainsKey("dados-sobre-mim.html"));
        using var manifest = JsonDocument.Parse(TextOf(entries["manifest.json"]));
        Assert.False(manifest.RootElement.TryGetProperty("receipt", out _));
    }
}
