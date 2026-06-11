using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;
using Xunit;

namespace M351.IntegrationTests;

/// <summary>
/// GET /api/v1/billing/billable-devices?month= (F3.7): regra de cobrável (não-archived com
/// eventos OU enroll OU last_seen_at no mês, no fuso do tenant) com evidence por device,
/// archived fora mesmo com eventos, device sem contato no mês fora, virada de mês UTC vs
/// local (GMT-3) no mês certo, 403 para Admin (papel da tabela 7.4 é Owner) e 400 para
/// month inválido/futuro.
///
/// Mês de referência FIXO: maio/2026 (passado, determinístico). Fuso default das orgs de
/// teste: America/Sao_Paulo (GMT-3, sem horário de verão desde 2019) — janela UTC de maio:
/// [2026-05-01T03:00Z, 2026-06-01T03:00Z). enrolled_at vem do timestamp do UUIDv7 do id,
/// por isso os devices são semeados por SQL com Uuid7.NewUuid7(instante do enroll).
/// </summary>
[Collection(ApiCollection.Name)]
public class BillingEndpointTests(ApiTestFixture fixture)
{
    private const string Maio = "2026-05";

    private static readonly DateTimeOffset Abril10 = DateTimeOffset.Parse("2026-04-10T12:00:00+00:00");
    private static readonly DateTimeOffset Abril25 = DateTimeOffset.Parse("2026-04-25T10:00:00+00:00");
    private static readonly DateTimeOffset Maio05 = DateTimeOffset.Parse("2026-05-05T12:00:00+00:00");
    private static readonly DateTimeOffset Maio15 = DateTimeOffset.Parse("2026-05-15T12:00:00+00:00");
    private static readonly DateTimeOffset Maio16 = DateTimeOffset.Parse("2026-05-16T12:00:00+00:00");
    private static readonly DateTimeOffset Maio20 = DateTimeOffset.Parse("2026-05-20T10:00:00+00:00");

    /// <summary>23:30 de 31/05 no fuso do tenant (GMT-3) = 02:30Z de 01/06 — a virada do caso de borda.</summary>
    private static readonly DateTimeOffset ViradaLocalMaio = DateTimeOffset.Parse("2026-06-01T02:30:00+00:00");

    private async Task<(HttpClient Client, Guid TenantId, string OwnerToken, string AdminToken)> SetupAsync(string prefix)
    {
        var org = await fixture.CreateOrganizationAsync($"{prefix} {Guid.NewGuid():N}"[..20]);
        var owner = await fixture.CreateUserAsync(org.Id, UserRole.Owner, mfaEnabled: true);
        var admin = await fixture.CreateUserAsync(org.Id, UserRole.Admin, mfaEnabled: true);
        var client = fixture.CreateApiClient();
        var ownerToken = await AuthClient.LoginAsync(client, owner);
        var adminToken = await AuthClient.LoginAsync(client, admin);
        return (client, org.Id, ownerToken, adminToken);
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

    /// <summary>
    /// Semeia device por SQL para controlar o instante do enroll: o id é Uuid7 do instante
    /// pedido (não existe coluna enrolled_at; o relatório extrai o timestamp do UUIDv7).
    /// </summary>
    private async Task<Guid> SeedDeviceAsync(
        Guid tenantId, string hostname, DateTimeOffset enrolledAt,
        string status = "active", DateTimeOffset? lastSeenAt = null)
    {
        var id = Uuid7.NewUuid7(enrolledAt);
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO devices (id, tenant_id, hostname, machine_fingerprint, token_hash, status, last_seen_at)
            VALUES (@id, @t, @h, @f, @k, @s, @l)
            """,
            ("id", id), ("t", tenantId), ("h", hostname), ("f", Guid.NewGuid().ToString("N")),
            ("k", new byte[32]), ("s", status), ("l", lastSeenAt?.UtcDateTime));
        return id;
    }

    /// <summary>
    /// Insere um raw_event garantindo as partições diárias do dia ±1 (a migration só cria as
    /// do mês da execução; aqui os dias são fixos de maio/junho de 2026). O ±1 espelha o
    /// RawEventPartitionManager: os limites das partições são datas interpretadas no fuso da
    /// SESSÃO do DDL, então o dia UTC do evento pode cair na partição vizinha.
    /// </summary>
    private async Task SeedRawEventAsync(Guid tenantId, Guid deviceId, DateTimeOffset occurredAt)
    {
        var dayUtc = DateOnly.FromDateTime(occurredAt.UtcDateTime);
        for (var day = dayUtc.AddDays(-1); day <= dayUtc.AddDays(1); day = day.AddDays(1))
        {
            await TestDb.ExecuteAsync(fixture.Database.ConnectionString,
                $"CREATE TABLE IF NOT EXISTS raw_events_{day:yyyyMMdd} PARTITION OF raw_events " +
                $"FOR VALUES FROM ('{day:yyyy-MM-dd}') TO ('{day.AddDays(1):yyyy-MM-dd}')");
        }
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO raw_events (tenant_id, device_id, event_id, seq, occurred_at, event_type)
            VALUES (@t, @d, @e, 1, @o, 'HEARTBEAT')
            """,
            ("t", tenantId), ("d", deviceId), ("e", Guid.NewGuid()), ("o", occurredAt.UtcDateTime));
    }

    private static Dictionary<Guid, JsonElement> ItemsById(JsonDocument doc) =>
        doc.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("device_id").GetGuid(), i => i);

    // ------------------------------------------------------------ regra de cobrável
    [Fact]
    public async Task RelatorioDeMaio_AplicaRegraDeCobravel_ComEvidenciaPorDevice()
    {
        var (client, tenantId, ownerToken, _) = await SetupAsync("BillRegra");

        // cobrável por eventos no mês (enroll antigo: a evidência tem que ser "events")
        var devEventos = await SeedDeviceAsync(tenantId, "NB-BILL-EVENTOS", Abril10, lastSeenAt: Maio15);
        await SeedRawEventAsync(tenantId, devEventos, Maio15);

        // archived NÃO conta MESMO com eventos no mês (spec linha 816: excluindo archived)
        var devArquivado = await SeedDeviceAsync(tenantId, "NB-BILL-ARQUIVADO", Abril10, status: "archived", lastSeenAt: Maio16);
        await SeedRawEventAsync(tenantId, devArquivado, Maio16);

        // só keep-alive no mês: lote vazio não gera raw_events, só atualiza last_seen_at
        var devKeepAlive = await SeedDeviceAsync(tenantId, "NB-BILL-KEEPALIVE", Abril10, lastSeenAt: Maio20);

        // enrolado no mês e silencioso desde então: conta por "enrolled"
        var devEnrolado = await SeedDeviceAsync(tenantId, "NB-BILL-ENROLADO", Maio05);

        // device de mês anterior SEM contato em maio: fora
        var devParado = await SeedDeviceAsync(tenantId, "NB-BILL-PARADO", Abril10, lastSeenAt: Abril25);

        var response = await SendAsync(client, HttpMethod.Get,
            $"/api/v1/billing/billable-devices?month={Maio}", ownerToken);
        using var doc = await ReadAsync(response, HttpStatusCode.OK);

        Assert.Equal(Maio, doc.RootElement.GetProperty("month").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("criteria").GetString()));

        var items = ItemsById(doc);
        Assert.Equal(3, doc.RootElement.GetProperty("device_count").GetInt32());
        Assert.Equal(3, items.Count);

        Assert.Equal("events", items[devEventos].GetProperty("evidence").GetString());
        Assert.Equal("keep_alive", items[devKeepAlive].GetProperty("evidence").GetString());
        Assert.Equal("enrolled", items[devEnrolado].GetProperty("evidence").GetString());

        Assert.DoesNotContain(devArquivado, items.Keys);
        Assert.DoesNotContain(devParado, items.Keys);

        // enrolled_at é reconstruído do UUIDv7 e tem que bater com o instante semeado
        Assert.Equal(Maio05, items[devEnrolado].GetProperty("enrolled_at").GetDateTimeOffset());
        Assert.Equal("NB-BILL-ENROLADO", items[devEnrolado].GetProperty("hostname").GetString());
        Assert.Equal("active", items[devEnrolado].GetProperty("status").GetString());
    }

    [Fact]
    public async Task ViradaDeMesUtc_ContaNoMesLocalDoTenant_GmtMenos3()
    {
        var (client, tenantId, ownerToken, _) = await SetupAsync("BillVirada");

        // evento às 23:30 LOCAIS de 31/05 (= 02:30Z de 01/06): em UTC já é junho, mas no
        // fuso do tenant ainda é maio — tem que contar em maio e NÃO contar em junho
        var devVirada = await SeedDeviceAsync(tenantId, "NB-BILL-VIRADA", Maio05, lastSeenAt: ViradaLocalMaio);
        await SeedRawEventAsync(tenantId, devVirada, ViradaLocalMaio);

        var maio = await SendAsync(client, HttpMethod.Get,
            $"/api/v1/billing/billable-devices?month={Maio}", ownerToken);
        using (var doc = await ReadAsync(maio, HttpStatusCode.OK))
        {
            var items = ItemsById(doc);
            Assert.Contains(devVirada, items.Keys);
            Assert.Equal("events", items[devVirada].GetProperty("evidence").GetString());
        }

        // junho/2026: evento, last_seen E enroll ficam todos antes de 2026-06-01T03:00Z
        var junho = await SendAsync(client, HttpMethod.Get,
            "/api/v1/billing/billable-devices?month=2026-06", ownerToken);
        using (var doc = await ReadAsync(junho, HttpStatusCode.OK))
        {
            Assert.DoesNotContain(devVirada, ItemsById(doc).Keys);
            Assert.Equal(0, doc.RootElement.GetProperty("device_count").GetInt32());
        }
    }

    // ------------------------------------------------------------ papel e validação
    [Fact]
    public async Task AdminRecebe403_PapelDaTabela74EOwner()
    {
        var (client, _, _, adminToken) = await SetupAsync("BillAdmin");

        var response = await SendAsync(client, HttpMethod.Get,
            $"/api/v1/billing/billable-devices?month={Maio}", adminToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MonthInvalido_Responde400()
    {
        var (client, _, ownerToken, _) = await SetupAsync("Bill400");

        foreach (var url in new[]
        {
            "/api/v1/billing/billable-devices",                  // ausente
            "/api/v1/billing/billable-devices?month=2026-13",    // mês impossível
            "/api/v1/billing/billable-devices?month=2026-6",     // sem zero à esquerda
            "/api/v1/billing/billable-devices?month=2026-06-01", // formato de data, não de mês
            "/api/v1/billing/billable-devices?month=maio",       // texto
        })
        {
            var response = await SendAsync(client, HttpMethod.Get, url, ownerToken);
            Assert.True(HttpStatusCode.BadRequest == response.StatusCode, $"esperado 400 para {url}, veio {response.StatusCode}");
        }
    }

    [Fact]
    public async Task MonthFuturo_Responde400()
    {
        var (client, _, ownerToken, _) = await SetupAsync("BillFuturo");

        // dois meses à frente do "agora" local do tenant (GMT-3): futuro em qualquer borda
        var futuro = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3)).AddMonths(2).ToString("yyyy-MM");
        var response = await SendAsync(client, HttpMethod.Get,
            $"/api/v1/billing/billable-devices?month={futuro}", ownerToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
