using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using M351.Domain;
using M351.Infrastructure.Exports;
using M351.IntegrationTests.Support;
using Npgsql;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// DSR completo (F4.5, Seções 7.4/8.7/9.3 — direitos do titular). Cobre:
///  - EXPORT subject: AdminPlus cria export_job (kind dsr_subject, queued) + trilha dsr_export;
///    o ExportService gera um ZIP (eventos/intervalos/agregados + manifest) SÓ do titular —
///    nunca vaza outro device_user/tenant; expires 72h; download application/zip;
///  - EXCLUSÃO subject (Owner): hard delete de raw_events/intervalos do titular, anonimização
///    da linha device_users, MANUTENÇÃO dos daily_* (recibo com contagens), trilha dsr_delete
///    com reason; confirmation/reason inválido → 400; Admin → 403; Viewer não exporta → 403;
///  - EXPORT/EXCLUSÃO cross-tenant → 404; tenant_full export.
///
/// O titular é um device_user. Datas dos eventos/intervalos no mês corrente (partições criadas
/// pela migration para o mês atual e o próximo).
/// </summary>
[Collection(ApiCollection.Name)]
public class DsrTests(ApiTestFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // ------------------------------------------------------------ setup de papéis
    private async Task<(HttpClient Client, Guid TenantId, string OwnerToken, string AdminToken, string ViewerToken)>
        SetupAsync(string prefix)
    {
        // Owner e Admin EXIGEM MFA configurada (senão login devolve mfa_setup_required)
        var org = await fixture.CreateOrganizationAsync($"{prefix} {Guid.NewGuid():N}"[..20]);
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var viewer = await fixture.CreateUserAsync(org.Id, UserRole.Viewer);
        var client = fixture.CreateApiClient();
        return (client, org.Id,
            await AuthClient.LoginAsync(client, owner),
            await AuthClient.LoginAsync(client, admin),
            await AuthClient.LoginAsync(client, viewer));
    }

    private async Task<int> RunExportWorkerOnceAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        return await new ExportService(dataSource, fixture.ExportsDirectory).RunOnceAsync();
    }

    private async Task DrainExportsAsync()
    {
        while (await RunExportWorkerOnceAsync() > 0) { }
    }

    /// <summary>Cria um device_user com SID determinístico e devolve (id, sid, windows_username).</summary>
    private async Task<(Guid Id, string Sid, string WindowsUsername)> SeedDeviceUserAsync(
        Guid tenantId, Guid deviceId, string displayName, string? sid = null, string? windowsUsername = null)
    {
        var id = Uuid7.NewUuid7();
        sid ??= $"S-1-5-21-DSR-{Guid.NewGuid():N}"[..40];
        windowsUsername ??= $"acme\\{displayName.ToLowerInvariant()}";
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO device_users (
                id, tenant_id, device_id, windows_sid, windows_username, display_name, first_seen_at, last_seen_at)
            VALUES (@id, @t, @d, @sid, @wu, @dn, now(), now())
            """,
            ("id", id), ("t", tenantId), ("d", deviceId), ("sid", sid), ("wu", windowsUsername), ("dn", displayName));
        return (id, sid, windowsUsername);
    }

    private async Task SeedRawEventAsync(
        Guid tenantId, Guid deviceId, string sid, string windowsUser, string title, DateTimeOffset at)
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO raw_events (
                tenant_id, device_id, event_id, seq, occurred_at, event_type,
                session_id, windows_sid, windows_username, process_name, window_title)
            VALUES (@t, @d, @e, @seq, @o, 'FOREGROUND', 1, @sid, @wu, 'chrome.exe', @title)
            """,
            ("t", tenantId), ("d", deviceId), ("e", Guid.NewGuid()), ("seq", DateTime.UtcNow.Ticks % 1_000_000),
            ("o", at.UtcDateTime), ("sid", sid), ("wu", windowsUser), ("title", title));
    }

    private async Task SeedIntervalAsync(
        Guid tenantId, Guid deviceId, Guid deviceUserId, string title, DateTimeOffset start)
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO activity_intervals (
                id, tenant_id, device_id, device_user_id, started_at, ended_at, state, window_title, source_day)
            VALUES (@id, @t, @d, @u, @s, @e, 'active', @title, @day::date)
            """,
            ("id", Uuid7.NewUuid7()), ("t", tenantId), ("d", deviceId), ("u", deviceUserId),
            ("s", start.UtcDateTime), ("e", start.AddMinutes(5).UtcDateTime), ("title", title),
            ("day", start.UtcDateTime.ToString("yyyy-MM-dd")));
    }

    private async Task SeedDailySummaryAsync(Guid tenantId, Guid deviceId, Guid deviceUserId, string date, int active)
    {
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO daily_device_summaries (
                tenant_id, summary_date, device_id, device_user_id,
                seconds_active, seconds_on, computed_at)
            VALUES (@t, @day::date, @d, @u, @a, @a, now())
            """,
            ("t", tenantId), ("day", date), ("d", deviceId), ("u", deviceUserId), ("a", active));
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, string token, object? body = null)
    {
        using var request = AuthClient.AuthorizedRequest(method, url, token, body);
        return await client.SendAsync(request);
    }

    /// <summary>Baixa o ZIP do pacote DSR e devolve nome→texto-decodificado de cada entry.</summary>
    private static async Task<Dictionary<string, string>> ReadZipEntriesAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entries = new Dictionary<string, string>();
        foreach (var entry in archive.Entries)
        {
            await using var stream = entry.Open();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var raw = ms.ToArray();
            // CSVs do pacote levam BOM UTF-8 (padrão do projeto); manifest.json é JSON puro
            var text = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF
                ? Encoding.UTF8.GetString(raw, 3, raw.Length - 3)
                : Encoding.UTF8.GetString(raw);
            entries[entry.FullName] = text;
        }

        return entries;
    }

    // ============================================================ EXPORT subject (fluxo completo)
    [Fact]
    public async Task ExportSubject_CriaJobETrilha_ZipSoDoTitular_Expira72h_DownloadZip()
    {
        var (client, tenantId, _, adminToken, _) = await SetupAsync("DsrExp");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-DSR-EXP");

        var ana = await SeedDeviceUserAsync(tenantId, device.Id, "Ana");
        var bob = await SeedDeviceUserAsync(tenantId, device.Id, "Bob"); // OUTRO titular: NÃO pode vazar

        var dateStr = Now.UtcDateTime.ToString("yyyy-MM-dd");
        await SeedRawEventAsync(tenantId, device.Id, ana.Sid, ana.WindowsUsername, "Relatorio Ana - Confidencial", Now);
        await SeedRawEventAsync(tenantId, device.Id, bob.Sid, bob.WindowsUsername, "Segredo do Bob", Now);
        await SeedIntervalAsync(tenantId, device.Id, ana.Id, "Janela Ana", Now);
        await SeedIntervalAsync(tenantId, device.Id, bob.Id, "Janela Bob", Now);
        await SeedDailySummaryAsync(tenantId, device.Id, ana.Id, dateStr, 3600);
        await SeedDailySummaryAsync(tenantId, device.Id, bob.Id, dateStr, 7200);

        // POST → 202 queued (AdminPlus) + trilha dsr_export
        var post = await SendAsync(client, HttpMethod.Post,
            $"/api/v1/privacy/subjects/{ana.Id}/export", adminToken);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        Guid jobId;
        using (var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
        {
            jobId = body.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("dsr_subject", body.RootElement.GetProperty("kind").GetString());
            Assert.Equal("queued", body.RootElement.GetProperty("status").GetString());
        }

        var audit = await TestDb.RowAsync(fixture.Database.ConnectionString, """
            SELECT action, detail->>'kind' AS kind, detail->>'device_user_id' AS du, target_id
            FROM audit_log WHERE tenant_id = @t AND action = 'dsr_export'
            """, ("t", tenantId));
        Assert.Equal("dsr_export", (string)audit!["action"]!);
        Assert.Equal("dsr_subject", (string)audit["kind"]!);
        Assert.Equal(ana.Id.ToString(), (string)audit["du"]!);
        Assert.Equal(ana.Id, (Guid)audit["target_id"]!);

        await DrainExportsAsync();

        var job = await TestDb.RowAsync(fixture.Database.ConnectionString,
            "SELECT status, file_path, expires_at FROM export_jobs WHERE id = @id", ("id", jobId));
        Assert.Equal("done", (string)job!["status"]!);
        Assert.Equal($"{tenantId}/{jobId}.zip", (string)job["file_path"]!); // .zip, não .csv
        var expiresAt = (DateTime)job["expires_at"]!;
        // 72h, não 7 dias
        Assert.InRange(expiresAt, DateTime.UtcNow.AddHours(70), DateTime.UtcNow.AddHours(74));

        // download → application/zip
        var download = await SendAsync(client, HttpMethod.Get, $"/api/v1/exports/{jobId}/download", adminToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType!.ToString());
        Assert.Contains($"dsr_subject_{jobId}.zip", download.Content.Headers.ContentDisposition!.ToString());

        var entries = await ReadZipEntriesAsync(download);
        Assert.True(entries.ContainsKey("eventos.csv"));
        Assert.True(entries.ContainsKey("intervalos.csv"));
        Assert.True(entries.ContainsKey("agregados.csv"));
        Assert.True(entries.ContainsKey("manifest.json"));

        // dados da Ana presentes (window_title do PRÓPRIO titular pode constar — lista fechada 9.1)
        Assert.Contains("Relatorio Ana - Confidencial", entries["eventos.csv"]);
        Assert.Contains("Janela Ana", entries["intervalos.csv"]);

        // dados do Bob (OUTRO titular) JAMAIS no pacote da Ana
        var allText = string.Join("\n", entries.Values);
        Assert.DoesNotContain("Segredo do Bob", allText);
        Assert.DoesNotContain("Janela Bob", allText);
        Assert.DoesNotContain(bob.WindowsUsername, allText);

        // manifest: só a Ana
        using var manifest = JsonDocument.Parse(entries["manifest.json"]);
        var subjects = manifest.RootElement.GetProperty("subjects").EnumerateArray().ToList();
        var subject = Assert.Single(subjects);
        Assert.Equal(ana.Id, subject.GetProperty("device_user_id").GetGuid());
        Assert.Equal(ana.WindowsUsername, subject.GetProperty("windows_username").GetString());
    }

    // ============================================================ EXCLUSÃO subject (Owner)
    [Fact]
    public async Task DeleteSubject_HardDelete_Anonimiza_MantemAgregados_ReciboETrilha()
    {
        var (client, tenantId, ownerToken, _, _) = await SetupAsync("DsrDel");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-DSR-DEL");
        var ana = await SeedDeviceUserAsync(tenantId, device.Id, "Ana");

        var dateStr = Now.UtcDateTime.ToString("yyyy-MM-dd");
        await SeedRawEventAsync(tenantId, device.Id, ana.Sid, ana.WindowsUsername, "Titulo bruto Ana", Now);
        await SeedRawEventAsync(tenantId, device.Id, ana.Sid, ana.WindowsUsername, "Outro titulo Ana", Now.AddMinutes(1));
        await SeedIntervalAsync(tenantId, device.Id, ana.Id, "Janela Ana", Now);
        await SeedDailySummaryAsync(tenantId, device.Id, ana.Id, dateStr, 3600);

        var delete = await SendAsync(client, HttpMethod.Delete, $"/api/v1/privacy/subjects/{ana.Id}/data", ownerToken,
            new { confirmation = ana.WindowsUsername, reason = "Solicitacao de exclusao do titular (art. 18 V)" });
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        using (var body = JsonDocument.Parse(await delete.Content.ReadAsStringAsync()))
        {
            var receipt = body.RootElement.GetProperty("receipt");
            Assert.Equal(2, receipt.GetProperty("raw_events_deleted").GetInt32());
            Assert.Equal(1, receipt.GetProperty("intervals_deleted").GetInt32());
            Assert.Equal(1, receipt.GetProperty("device_users_anonymized").GetInt32());
            Assert.Equal(1, receipt.GetProperty("daily_rows_kept").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(receipt.GetProperty("note").GetString()));
        }

        // raw_events e intervalos do titular APAGADOS
        Assert.Equal(0L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM raw_events WHERE device_id = @d AND windows_sid = @sid",
            ("d", device.Id), ("sid", ana.Sid)));
        Assert.Equal(0L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM activity_intervals WHERE device_user_id = @u", ("u", ana.Id)));

        // device_users ANONIMIZADO (linha preservada, identidade neutra)
        var du = await TestDb.RowAsync(fixture.Database.ConnectionString,
            "SELECT windows_username, windows_sid, display_name FROM device_users WHERE id = @u", ("u", ana.Id));
        Assert.NotNull(du);
        Assert.DoesNotContain("ana", ((string)du!["windows_username"]!).ToLowerInvariant());
        Assert.NotEqual(ana.Sid, (string)du["windows_sid"]!);
        Assert.Equal("Usuário removido (DSR)", (string)du["display_name"]!);

        // daily_* MANTIDO (agregado de equipe não some — Seção 9.3 linha 995)
        Assert.Equal(1L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM daily_device_summaries WHERE device_user_id = @u", ("u", ana.Id)));

        // trilha dsr_delete com reason e recibo
        var audit = await TestDb.RowAsync(fixture.Database.ConnectionString, """
            SELECT detail->>'reason' AS reason, detail->'receipt'->>'raw_events_deleted' AS raw, target_id
            FROM audit_log WHERE tenant_id = @t AND action = 'dsr_delete'
            """, ("t", tenantId));
        Assert.Contains("exclusao do titular", (string)audit!["reason"]!);
        Assert.Equal("2", (string)audit["raw"]!);
        Assert.Equal(ana.Id, (Guid)audit["target_id"]!);
    }

    // ------------------------------------------------------------ confirmation / reason inválidos
    [Fact]
    public async Task DeleteSubject_ConfirmationOuReasonInvalido_400_SemEfeito()
    {
        var (client, tenantId, ownerToken, _, _) = await SetupAsync("DsrInv");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-DSR-INV");
        var ana = await SeedDeviceUserAsync(tenantId, device.Id, "Ana");
        await SeedRawEventAsync(tenantId, device.Id, ana.Sid, ana.WindowsUsername, "Titulo", Now);

        // confirmation que não bate
        var wrongConfirm = await SendAsync(client, HttpMethod.Delete, $"/api/v1/privacy/subjects/{ana.Id}/data", ownerToken,
            new { confirmation = "valor-errado", reason = "Motivo suficientemente longo" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongConfirm.StatusCode);

        // reason curto
        var shortReason = await SendAsync(client, HttpMethod.Delete, $"/api/v1/privacy/subjects/{ana.Id}/data", ownerToken,
            new { confirmation = ana.WindowsUsername, reason = "curto" });
        Assert.Equal(HttpStatusCode.BadRequest, shortReason.StatusCode);

        // reason ausente
        var noReason = await SendAsync(client, HttpMethod.Delete, $"/api/v1/privacy/subjects/{ana.Id}/data", ownerToken,
            new { confirmation = ana.WindowsUsername });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        // nada foi apagado e nenhuma trilha dsr_delete
        Assert.Equal(1L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM raw_events WHERE device_id = @d AND windows_sid = @sid",
            ("d", device.Id), ("sid", ana.Sid)));
        Assert.Equal(0L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM audit_log WHERE tenant_id = @t AND action = 'dsr_delete'", ("t", tenantId)));
    }

    // ------------------------------------------------------------ RBAC: Admin não exclui, Viewer não exporta
    [Fact]
    public async Task Rbac_AdminNaoExclui403_ViewerNaoExporta403()
    {
        var (client, tenantId, _, adminToken, viewerToken) = await SetupAsync("DsrRbac");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-DSR-RBAC");
        var ana = await SeedDeviceUserAsync(tenantId, device.Id, "Ana");

        // Admin tenta EXCLUIR (OwnerOnly) → 403
        var adminDelete = await SendAsync(client, HttpMethod.Delete, $"/api/v1/privacy/subjects/{ana.Id}/data", adminToken,
            new { confirmation = ana.WindowsUsername, reason = "Motivo suficientemente longo" });
        Assert.Equal(HttpStatusCode.Forbidden, adminDelete.StatusCode);

        // Viewer tenta EXPORTAR (AdminPlus) → 403
        var viewerExport = await SendAsync(client, HttpMethod.Post, $"/api/v1/privacy/subjects/{ana.Id}/export", viewerToken);
        Assert.Equal(HttpStatusCode.Forbidden, viewerExport.StatusCode);

        // Viewer tenta full-export (OwnerOnly) → 403
        var viewerFull = await SendAsync(client, HttpMethod.Post, "/api/v1/privacy/tenant/full-export", viewerToken);
        Assert.Equal(HttpStatusCode.Forbidden, viewerFull.StatusCode);
    }

    // ------------------------------------------------------------ device export + delete
    [Fact]
    public async Task ExportEDeleteDevice_AplicaATodosOsTitularesDoDevice()
    {
        var (client, tenantId, ownerToken, adminToken, _) = await SetupAsync("DsrDev");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-DSR-DEVICE");
        var ana = await SeedDeviceUserAsync(tenantId, device.Id, "Ana");
        var bob = await SeedDeviceUserAsync(tenantId, device.Id, "Bob");
        await SeedRawEventAsync(tenantId, device.Id, ana.Sid, ana.WindowsUsername, "Ana", Now);
        await SeedRawEventAsync(tenantId, device.Id, bob.Sid, bob.WindowsUsername, "Bob", Now);
        await SeedIntervalAsync(tenantId, device.Id, ana.Id, "Janela Ana", Now);
        await SeedIntervalAsync(tenantId, device.Id, bob.Id, "Janela Bob", Now);

        // export device (AdminPlus) → 202 dsr_device
        var export = await SendAsync(client, HttpMethod.Post, $"/api/v1/privacy/devices/{device.Id}/export", adminToken);
        Assert.Equal(HttpStatusCode.Accepted, export.StatusCode);
        using (var body = JsonDocument.Parse(await export.Content.ReadAsStringAsync()))
        {
            Assert.Equal("dsr_device", body.RootElement.GetProperty("kind").GetString());
        }

        // delete device (Owner): confirmation = hostname
        var delete = await SendAsync(client, HttpMethod.Delete, $"/api/v1/privacy/devices/{device.Id}/data", ownerToken,
            new { confirmation = "NB-DSR-DEVICE", reason = "Descarte do equipamento e dos titulares" });
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        using (var body = JsonDocument.Parse(await delete.Content.ReadAsStringAsync()))
        {
            var receipt = body.RootElement.GetProperty("receipt");
            Assert.Equal(2, receipt.GetProperty("raw_events_deleted").GetInt32());
            Assert.Equal(2, receipt.GetProperty("intervals_deleted").GetInt32());
            Assert.Equal(2, receipt.GetProperty("device_users_anonymized").GetInt32());
        }

        // ambos os titulares anonimizados
        Assert.Equal(2L, await TestDb.ScalarAsync<long>(fixture.Database.ConnectionString,
            "SELECT count(*) FROM device_users WHERE device_id = @d AND display_name = 'Usuário removido (DSR)'",
            ("d", device.Id)));
    }

    // ------------------------------------------------------------ tenant full-export
    [Fact]
    public async Task TenantFullExport_CriaJobTenantFull_GeraZip()
    {
        var (client, tenantId, ownerToken, _, _) = await SetupAsync("DsrFull");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-DSR-FULL");
        var ana = await SeedDeviceUserAsync(tenantId, device.Id, "Ana");
        await SeedRawEventAsync(tenantId, device.Id, ana.Sid, ana.WindowsUsername, "Evento tenant", Now);
        await SeedIntervalAsync(tenantId, device.Id, ana.Id, "Janela tenant", Now);

        var post = await SendAsync(client, HttpMethod.Post, "/api/v1/privacy/tenant/full-export", ownerToken);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        Guid jobId;
        using (var body = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
        {
            jobId = body.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("tenant_full", body.RootElement.GetProperty("kind").GetString());
        }

        await DrainExportsAsync();

        var download = await SendAsync(client, HttpMethod.Get, $"/api/v1/exports/{jobId}/download", ownerToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType!.ToString());

        var entries = await ReadZipEntriesAsync(download);
        Assert.Contains("Evento tenant", entries["eventos.csv"]);
        Assert.Contains("Janela tenant", entries["intervalos.csv"]);
    }

    // ============================================================ gate de papel no DOWNLOAD/LISTAGEM do pacote DSR
    /// <summary>
    /// O ARTEFATO DSR (ZIP com window_title/eventos brutos do titular) é servido pelo
    /// ExportsController e herda o MESMO papel da criação em /privacy/*: dsr_subject/dsr_device =
    /// AdminPlus, tenant_full = OwnerOnly. Um Viewer NÃO pode listar nem baixar o pacote (senão
    /// contornaria o gate AdminPlus da criação), e um Admin não alcança o tenant_full (OwnerOnly).
    /// Papel insuficiente → 404 (Princípio 4), e o job some da GET /exports.
    /// </summary>
    [Fact]
    public async Task DownloadEListagem_PacoteDsr_RespeitamPapelDaCriacao()
    {
        var (client, tenantId, ownerToken, adminToken, viewerToken) = await SetupAsync("DsrGate");
        var device = await fixture.CreateDeviceAsync(tenantId, "NB-DSR-GATE");
        var ana = await SeedDeviceUserAsync(tenantId, device.Id, "Ana");
        await SeedRawEventAsync(tenantId, device.Id, ana.Sid, ana.WindowsUsername, "Titulo bruto da Ana", Now);
        await SeedIntervalAsync(tenantId, device.Id, ana.Id, "Janela Ana", Now);

        // Admin cria o pacote do titular; Owner cria o tenant_full
        Guid subjectJobId;
        var subjectPost = await SendAsync(client, HttpMethod.Post, $"/api/v1/privacy/subjects/{ana.Id}/export", adminToken);
        using (var body = JsonDocument.Parse(await subjectPost.Content.ReadAsStringAsync()))
            subjectJobId = body.RootElement.GetProperty("id").GetGuid();

        Guid tenantFullJobId;
        var fullPost = await SendAsync(client, HttpMethod.Post, "/api/v1/privacy/tenant/full-export", ownerToken);
        using (var body = JsonDocument.Parse(await fullPost.Content.ReadAsStringAsync()))
            tenantFullJobId = body.RootElement.GetProperty("id").GetGuid();

        await DrainExportsAsync();

        // ---- Viewer: não vê NENHUM pacote DSR na listagem e não baixa (404, não 403)
        var viewerList = await SendAsync(client, HttpMethod.Get, "/api/v1/exports", viewerToken);
        Assert.Equal(HttpStatusCode.OK, viewerList.StatusCode);
        using (var body = JsonDocument.Parse(await viewerList.Content.ReadAsStringAsync()))
        {
            var ids = body.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("id").GetGuid()).ToHashSet();
            Assert.DoesNotContain(subjectJobId, ids);
            Assert.DoesNotContain(tenantFullJobId, ids);
        }
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(client, HttpMethod.Get, $"/api/v1/exports/{subjectJobId}/download", viewerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(client, HttpMethod.Get, $"/api/v1/exports/{subjectJobId}", viewerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(client, HttpMethod.Get, $"/api/v1/exports/{tenantFullJobId}/download", viewerToken)).StatusCode);

        // ---- Admin: alcança dsr_subject/dsr_device, mas NÃO o tenant_full (OwnerOnly)
        var adminList = await SendAsync(client, HttpMethod.Get, "/api/v1/exports", adminToken);
        using (var body = JsonDocument.Parse(await adminList.Content.ReadAsStringAsync()))
        {
            var ids = body.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("id").GetGuid()).ToHashSet();
            Assert.Contains(subjectJobId, ids);
            Assert.DoesNotContain(tenantFullJobId, ids);
        }
        var adminSubject = await SendAsync(client, HttpMethod.Get, $"/api/v1/exports/{subjectJobId}/download", adminToken);
        Assert.Equal(HttpStatusCode.OK, adminSubject.StatusCode);
        Assert.Equal("application/zip", adminSubject.Content.Headers.ContentType!.ToString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(client, HttpMethod.Get, $"/api/v1/exports/{tenantFullJobId}/download", adminToken)).StatusCode);

        // ---- Owner: alcança tudo, inclusive o tenant_full
        var ownerList = await SendAsync(client, HttpMethod.Get, "/api/v1/exports", ownerToken);
        using (var body = JsonDocument.Parse(await ownerList.Content.ReadAsStringAsync()))
        {
            var ids = body.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("id").GetGuid()).ToHashSet();
            Assert.Contains(subjectJobId, ids);
            Assert.Contains(tenantFullJobId, ids);
        }
        var ownerFull = await SendAsync(client, HttpMethod.Get, $"/api/v1/exports/{tenantFullJobId}/download", ownerToken);
        Assert.Equal(HttpStatusCode.OK, ownerFull.StatusCode);
        Assert.Equal("application/zip", ownerFull.Content.Headers.ContentType!.ToString());
    }

    // ============================================================ cross-tenant → 404
    [Fact]
    public async Task CrossTenant_ExportEDelete_404()
    {
        var (clientA, _, ownerA, adminA, _) = await SetupAsync("DsrIsoA");
        var (_, tenantB, _, _, _) = await SetupAsync("DsrIsoB");
        var deviceB = await fixture.CreateDeviceAsync(tenantB, "NB-DSR-ISO-B");
        var anaB = await SeedDeviceUserAsync(tenantB, deviceB.Id, "AnaB");

        // export subject de B autenticado em A → 404
        var exportSubject = await SendAsync(clientA, HttpMethod.Post,
            $"/api/v1/privacy/subjects/{anaB.Id}/export", adminA);
        Assert.Equal(HttpStatusCode.NotFound, exportSubject.StatusCode);

        // export device de B → 404
        var exportDevice = await SendAsync(clientA, HttpMethod.Post,
            $"/api/v1/privacy/devices/{deviceB.Id}/export", adminA);
        Assert.Equal(HttpStatusCode.NotFound, exportDevice.StatusCode);

        // delete subject de B → 404 (nunca 403, mesmo com confirmation/reason válidos)
        var deleteSubject = await SendAsync(clientA, HttpMethod.Delete,
            $"/api/v1/privacy/subjects/{anaB.Id}/data", ownerA,
            new { confirmation = anaB.WindowsUsername, reason = "Tentativa cross-tenant que deve falhar" });
        Assert.Equal(HttpStatusCode.NotFound, deleteSubject.StatusCode);

        // delete device de B → 404
        var deleteDevice = await SendAsync(clientA, HttpMethod.Delete,
            $"/api/v1/privacy/devices/{deviceB.Id}/data", ownerA,
            new { confirmation = "NB-DSR-ISO-B", reason = "Tentativa cross-tenant que deve falhar" });
        Assert.Equal(HttpStatusCode.NotFound, deleteDevice.StatusCode);

        // o titular de B continua intacto: nada apagado, nada anonimizado
        var du = await TestDb.RowAsync(fixture.Database.ConnectionString,
            "SELECT display_name FROM device_users WHERE id = @u", ("u", anaB.Id));
        Assert.Equal("AnaB", (string)du!["display_name"]!);
    }
}
