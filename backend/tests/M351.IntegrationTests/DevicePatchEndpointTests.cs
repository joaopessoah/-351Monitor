using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// PATCH /api/v1/devices/{id} (F3.7): atualização parcial campo a campo e combinada,
/// display_name null limpando o apelido, transições de status do portal
/// (active|paused|archived), revoked terminal (400), 403 do viewer, audit update_device
/// com de→para por campo alterado, e o ?include_archived do GET /devices (default true
/// preserva o comportamento; false esconde archived; ?status=archived o ignora).
/// </summary>
[Collection(ApiCollection.Name)]
public class DevicePatchEndpointTests(ApiTestFixture fixture)
{
    private async Task<(HttpClient Client, Guid TenantId, string AdminToken, string ViewerToken)> SetupAsync(string prefix)
    {
        var org = await fixture.CreateOrganizationAsync($"{prefix} {Guid.NewGuid():N}"[..20]);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        var adminToken = await AuthClient.LoginAsync(client, admin);
        var viewerToken = await AuthClient.LoginAsync(client, viewer);
        return (client, org.Id, adminToken, viewerToken);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, string token, object? body = null)
    {
        using var request = AuthClient.AuthorizedRequest(method, url, token, body);
        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"esperado {expected}, veio {response.StatusCode}: {body}");
        return JsonDocument.Parse(string.IsNullOrEmpty(body) ? "null" : body);
    }

    private async Task<long> UpdateDeviceAuditCountAsync(Guid deviceId) =>
        await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE action = 'update_device' AND target_id = @d", ("d", deviceId));

    private async Task<JsonDocument?> LastUpdateDeviceAuditDetailAsync(Guid deviceId)
    {
        var detail = await TestDb.ScalarAsync<string>(fixture.Database.ConnectionString, """
            SELECT detail::text FROM audit_log
            WHERE action = 'update_device' AND target_id = @d
            ORDER BY occurred_at DESC, id DESC LIMIT 1
            """, ("d", deviceId));
        return detail is null ? null : JsonDocument.Parse(detail);
    }

    // ------------------------------------------------------------ campos isolados
    [Fact]
    public async Task PatchDisplayName_AtualizaSoEsseCampo_EAuditaDePara()
    {
        var (client, tenantId, adminToken, _) = await SetupAsync("DevPatchNome");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-PATCH-NOME");

        var response = await SendAsync(client, HttpMethod.Patch, $"/api/v1/devices/{device.Id}", adminToken,
            new { display_name = "Notebook da Ana" });
        using (var doc = await ReadAsync(response, HttpStatusCode.OK))
        {
            Assert.Equal("Notebook da Ana", doc.RootElement.GetProperty("display_name").GetString());
            Assert.Equal("NB-PATCH-NOME", doc.RootElement.GetProperty("hostname").GetString());
            Assert.Equal("active", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("tags").ValueKind);
        }

        Assert.Equal(1L, await UpdateDeviceAuditCountAsync(device.Id));
        using var detail = await LastUpdateDeviceAuditDetailAsync(device.Id);
        var change = detail!.RootElement.GetProperty("display_name");
        Assert.Equal(JsonValueKind.Null, change.GetProperty("from").ValueKind);
        Assert.Equal("Notebook da Ana", change.GetProperty("to").GetString());
    }

    [Fact]
    public async Task PatchDisplayNameNull_LimpaOApelido()
    {
        var (client, tenantId, adminToken, _) = await SetupAsync("DevPatchLimpa");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-PATCH-LIMPA");

        var batiza = await SendAsync(client, HttpMethod.Patch, $"/api/v1/devices/{device.Id}", adminToken,
            new { display_name = "Apelido Temporario" });
        (await ReadAsync(batiza, HttpStatusCode.OK)).Dispose();

        var limpa = await SendAsync(client, HttpMethod.Patch, $"/api/v1/devices/{device.Id}", adminToken,
            new { display_name = (string?)null });
        using (var doc = await ReadAsync(limpa, HttpStatusCode.OK))
        {
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("display_name").ValueKind);
        }

        // de→para da limpeza fica na trilha
        using var detail = await LastUpdateDeviceAuditDetailAsync(device.Id);
        var change = detail!.RootElement.GetProperty("display_name");
        Assert.Equal("Apelido Temporario", change.GetProperty("from").GetString());
        Assert.Equal(JsonValueKind.Null, change.GetProperty("to").ValueKind);
    }

    [Fact]
    public async Task PatchTags_SubstituiAListaInteira()
    {
        var (client, tenantId, adminToken, _) = await SetupAsync("DevPatchTags");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-PATCH-TAGS");

        var primeira = await SendAsync(client, HttpMethod.Patch, $"/api/v1/devices/{device.Id}", adminToken,
            new { tags = new[] { "fiscal", "matriz" } });
        using (var doc = await ReadAsync(primeira, HttpStatusCode.OK))
        {
            Assert.Equal(new[] { "fiscal", "matriz" },
                doc.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray());
        }

        var segunda = await SendAsync(client, HttpMethod.Patch, $"/api/v1/devices/{device.Id}", adminToken,
            new { tags = new[] { "filial" } });
        using (var doc = await ReadAsync(segunda, HttpStatusCode.OK))
        {
            Assert.Equal(new[] { "filial" },
                doc.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray());
        }

        using var detail = await LastUpdateDeviceAuditDetailAsync(device.Id);
        var change = detail!.RootElement.GetProperty("tags");
        Assert.Equal(new[] { "fiscal", "matriz" },
            change.GetProperty("from").EnumerateArray().Select(t => t.GetString()).ToArray());
        Assert.Equal(new[] { "filial" },
            change.GetProperty("to").EnumerateArray().Select(t => t.GetString()).ToArray());
    }

    [Fact]
    public async Task PatchStatus_ArquivaDesarquivaEPausa()
    {
        var (client, tenantId, adminToken, _) = await SetupAsync("DevPatchStatus");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-PATCH-STATUS");

        foreach (var status in new[] { "archived", "active", "paused" })
        {
            var response = await SendAsync(client, HttpMethod.Patch, $"/api/v1/devices/{device.Id}", adminToken,
                new { status });
            using var doc = await ReadAsync(response, HttpStatusCode.OK);
            Assert.Equal(status, doc.RootElement.GetProperty("status").GetString());
        }

        Assert.Equal(3L, await UpdateDeviceAuditCountAsync(device.Id));
        using var detail = await LastUpdateDeviceAuditDetailAsync(device.Id);
        var change = detail!.RootElement.GetProperty("status");
        Assert.Equal("active", change.GetProperty("from").GetString());
        Assert.Equal("paused", change.GetProperty("to").GetString());
    }

    // ------------------------------------------------------------ combinado + sem mudança
    [Fact]
    public async Task PatchCombinado_AtualizaTudo_ComUmUnicoAuditDeParaPorCampo()
    {
        var (client, tenantId, adminToken, _) = await SetupAsync("DevPatchCombo");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-PATCH-COMBO");

        var response = await SendAsync(client, HttpMethod.Patch, $"/api/v1/devices/{device.Id}", adminToken,
            new { display_name = "Recepcao", tags = new[] { "terreo" }, status = "paused" });
        using (var doc = await ReadAsync(response, HttpStatusCode.OK))
        {
            Assert.Equal("Recepcao", doc.RootElement.GetProperty("display_name").GetString());
            Assert.Equal("paused", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal(new[] { "terreo" },
                doc.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray());
        }

        Assert.Equal(1L, await UpdateDeviceAuditCountAsync(device.Id));
        using var detail = await LastUpdateDeviceAuditDetailAsync(device.Id);
        Assert.True(detail!.RootElement.TryGetProperty("display_name", out _));
        Assert.True(detail.RootElement.TryGetProperty("tags", out _));
        Assert.True(detail.RootElement.TryGetProperty("status", out _));
    }

    [Fact]
    public async Task PatchSemMudancaEfetiva_Responde200_SemAudit()
    {
        var (client, tenantId, adminToken, _) = await SetupAsync("DevPatchNoop");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-PATCH-NOOP");

        // corpo vazio E corpo repetindo os valores atuais: nada muda, nada é auditado
        var vazio = await SendAsync(client, HttpMethod.Patch, $"/api/v1/devices/{device.Id}", adminToken, new { });
        (await ReadAsync(vazio, HttpStatusCode.OK)).Dispose();

        var igual = await SendAsync(client, HttpMethod.Patch, $"/api/v1/devices/{device.Id}", adminToken,
            new { status = "active", display_name = (string?)null });
        using (var doc = await ReadAsync(igual, HttpStatusCode.OK))
        {
            Assert.Equal("active", doc.RootElement.GetProperty("status").GetString());
        }

        Assert.Equal(0L, await UpdateDeviceAuditCountAsync(device.Id));
    }

    // ------------------------------------------------------------ erros
    [Fact]
    public async Task PatchStatusInvalido_Responde400()
    {
        var (client, tenantId, adminToken, _) = await SetupAsync("DevPatch400");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-PATCH-400");

        // revoked NÃO entra pelo PATCH (fluxo próprio de revogação) e valor desconhecido é 400
        foreach (var status in new[] { "revoked", "banana" })
        {
            var response = await SendAsync(client, HttpMethod.Patch, $"/api/v1/devices/{device.Id}", adminToken,
                new { status });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(0L, await UpdateDeviceAuditCountAsync(device.Id));
    }

    [Fact]
    public async Task PatchDeviceRevogado_Responde400_RevokedETerminal()
    {
        var (client, tenantId, adminToken, _) = await SetupAsync("DevPatchRev");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-PATCH-REVOGADO");

        var revoke = await SendAsync(client, HttpMethod.Post, $"/api/v1/devices/{device.Id}/revoke", adminToken);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var response = await SendAsync(client, HttpMethod.Patch, $"/api/v1/devices/{device.Id}", adminToken,
            new { display_name = "Tentativa" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // permanece revoked e sem apelido
        var status = await TestDb.ScalarAsync<string>(fixture.Database.ConnectionString,
            "SELECT status FROM devices WHERE id = @d", ("d", device.Id));
        Assert.Equal("revoked", status);
    }

    [Fact]
    public async Task PatchComoViewer_Responde403()
    {
        var (client, tenantId, _, viewerToken) = await SetupAsync("DevPatchViewer");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-PATCH-VIEWER");

        var response = await SendAsync(client, HttpMethod.Patch, $"/api/v1/devices/{device.Id}", viewerToken,
            new { display_name = "Sem permissao" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------ GET /devices?include_archived
    [Fact]
    public async Task IncludeArchived_DefaultMantemArchived_FalseEsconde_EStatusArchivedIgnora()
    {
        var (client, tenantId, adminToken, _) = await SetupAsync("DevListArq");
        var ativo = await fixture.CreateDeviceAsync(tenantId, "NB-LISTA-ATIVO");
        var arquivado = await fixture.CreateDeviceAsync(tenantId, "NB-LISTA-ARQUIVADO");

        var arquiva = await SendAsync(client, HttpMethod.Patch, $"/api/v1/devices/{arquivado.Id}", adminToken,
            new { status = "archived" });
        (await ReadAsync(arquiva, HttpStatusCode.OK)).Dispose();

        static List<Guid> Ids(JsonDocument doc) =>
            doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(d => d.GetProperty("id").GetGuid()).ToList();

        // default (sem o parâmetro): comportamento de antes, archived aparece
        var padrao = await SendAsync(client, HttpMethod.Get, "/api/v1/devices?page_size=100", adminToken);
        using (var doc = await ReadAsync(padrao, HttpStatusCode.OK))
        {
            Assert.Contains(ativo.Id, Ids(doc));
            Assert.Contains(arquivado.Id, Ids(doc));
        }

        // include_archived=false: archived some da listagem
        var semArquivados = await SendAsync(client, HttpMethod.Get,
            "/api/v1/devices?page_size=100&include_archived=false", adminToken);
        using (var doc = await ReadAsync(semArquivados, HttpStatusCode.OK))
        {
            Assert.Contains(ativo.Id, Ids(doc));
            Assert.DoesNotContain(arquivado.Id, Ids(doc));
        }

        // ?status=archived continua funcionando e IGNORA include_archived
        var soArquivados = await SendAsync(client, HttpMethod.Get,
            "/api/v1/devices?page_size=100&status=archived&include_archived=false", adminToken);
        using (var doc = await ReadAsync(soArquivados, HttpStatusCode.OK))
        {
            Assert.Contains(arquivado.Id, Ids(doc));
            Assert.DoesNotContain(ativo.Id, Ids(doc));
        }
    }
}
