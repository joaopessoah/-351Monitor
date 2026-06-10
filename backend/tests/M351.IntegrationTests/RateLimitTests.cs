using System.Net;
using M351.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace M351.IntegrationTests;

/// <summary>
/// Rate limiting da F1 (Seções 5.6/5.7): enroll 10/min por IP, ingestão token bucket por device
/// (sustentado + burst) e cota diária dura de eventos ACEITOS — todos → 429 ProblemDetails com
/// Retry-After em segundos. A fixture base roda com RateLimiting:Enabled=false; cada teste sobe
/// um host derivado (WithWebHostBuilder) com limites BAIXOS específicos do cenário.
/// </summary>
[Collection(ApiCollection.Name)]
public class RateLimitTests(ApiTestFixture fixture)
{
    private string Cs => fixture.Database.ConnectionString;

    /// <summary>Host derivado com rate limiting LIGADO e overrides de limites do cenário.</summary>
    private WebApplicationFactory<Program> EnabledFactory(params (string Key, string Value)[] limits) =>
        fixture.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:Enabled", "true");
            foreach (var (key, value) in limits)
            {
                builder.UseSetting($"RateLimiting:{key}", value);
            }
        });

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static int RetryAfterSeconds(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Retry-After", out var values),
            "Resposta 429 sem header Retry-After.");
        return int.Parse(values!.Single(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task AssertProblem429Async(HttpResponseMessage response, string expectedReason)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(expectedReason, await response.Content.ReadAsStringAsync());
    }

    // ----- Seção 5.7: enroll por IP -----

    [Fact]
    public async Task Enroll_AlemDoLimitePorIp_Retorna429ComRetryAfter()
    {
        using var factory = EnabledFactory(("EnrollPerMinutePerIp", "2"));
        var client = ClientFor(factory);
        var org = await fixture.CreateOrganizationAsync("Org RL Enroll");
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);

        var first = await AgentClient.EnrollRawAsync(client, fullKey, AgentClient.NewFingerprint());
        var second = await AgentClient.EnrollRawAsync(client, fullKey, AgentClient.NewFingerprint());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var third = await AgentClient.EnrollRawAsync(client, fullKey, AgentClient.NewFingerprint());
        await AssertProblem429Async(third, "rate_limited");
        Assert.InRange(RetryAfterSeconds(third), 1, 60); // reset da janela fixa de 1 min

        // o excedente NÃO virou device
        var count = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM devices WHERE tenant_id = @t", ("t", org.Id));
        Assert.Equal(2, count);
    }

    // ----- Seção 5.6: token bucket por device -----

    [Fact]
    public async Task Ingest_TerceiroLoteAlemDoBurst_Retorna429_SemAfetarOutroDeviceDoTenant()
    {
        using var factory = EnabledFactory(
            ("IngestBurstPerDevice", "2"),
            ("IngestPerMinutePerDevice", "1"), // reabastecimento lento: nenhum token volta no teste
            ("EnrollPerMinutePerIp", "100"));
        var client = ClientFor(factory);
        var org = await fixture.CreateOrganizationAsync("Org RL Burst");
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var deviceA = await AgentClient.EnrollAsync(client, fullKey);
        var deviceB = await AgentClient.EnrollAsync(client, fullKey);

        var first = await AgentClient.SendBatchAsync(client, deviceA.DeviceToken, []);
        var second = await AgentClient.SendBatchAsync(client, deviceA.DeviceToken, []);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var third = await AgentClient.SendBatchAsync(client, deviceA.DeviceToken, []);
        await AssertProblem429Async(third, "rate_limited");
        Assert.True(RetryAfterSeconds(third) >= 1);

        // o bucket é POR DEVICE: outro device do MESMO tenant segue aceitando
        var otherDevice = await AgentClient.SendBatchAsync(client, deviceB.DeviceToken, []);
        Assert.Equal(HttpStatusCode.OK, otherDevice.StatusCode);
    }

    [Fact]
    public async Task Ingest_DentroDoLimiteAposReabastecimento_Retorna200()
    {
        using var factory = EnabledFactory(
            ("IngestBurstPerDevice", "1"),
            ("IngestPerMinutePerDevice", "20"), // 1 token a cada 3 s — janela curta p/ o teste
            ("EnrollPerMinutePerIp", "100"));
        var client = ClientFor(factory);
        var org = await fixture.CreateOrganizationAsync("Org RL Janela");
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var device = await AgentClient.EnrollAsync(client, fullKey);

        // aquecimento: consome o único token e compila o caminho da ingestão
        var warmup = await AgentClient.SendBatchAsync(client, device.DeviceToken, []);
        Assert.Equal(HttpStatusCode.OK, warmup.StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(4)); // bucket reabastecido (cap = 1)

        var allowed = await AgentClient.SendBatchAsync(client, device.DeviceToken, []);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        var blocked = await AgentClient.SendBatchAsync(client, device.DeviceToken, []);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(4)); // após a janela, volta a aceitar
        var afterWindow = await AgentClient.SendBatchAsync(client, device.DeviceToken, []);
        Assert.Equal(HttpStatusCode.OK, afterWindow.StatusCode);
    }

    // ----- Seção 5.6: cota diária dura por device -----

    [Fact]
    public async Task Ingest_CotaDiaria_BloqueiaExcedenteSemPersistir_DuplicataNaoConsome()
    {
        using var factory = EnabledFactory(
            ("DailyEventQuotaPerDevice", "10"),
            ("IngestPerMinutePerDevice", "600"), // token bucket folgado: só a cota atua aqui
            ("IngestBurstPerDevice", "100"),
            ("EnrollPerMinutePerIp", "100"));
        var client = ClientFor(factory);
        var org = await fixture.CreateOrganizationAsync("Org RL Cota");
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var deviceA = await AgentClient.EnrollAsync(client, fullKey);
        var deviceB = await AgentClient.EnrollAsync(client, fullKey);

        var events = new EventFactory();
        var at = DateTimeOffset.UtcNow.AddMinutes(-5);
        var firstBatch = Enumerable.Range(0, 5).Select(_ => events.Event("HEARTBEAT", at)).ToList();

        // 5 aceitos → cota usada: 5/10
        using (var ack = await AgentClient.ReadAckAsync(
            await AgentClient.SendBatchAsync(client, deviceA.DeviceToken, firstBatch)))
        {
            Assert.Equal(5, ack.RootElement.GetProperty("accepted").GetInt32());
        }

        // REENVIO do mesmo lote: 5 duplicatas, 0 aceitos — duplicata NÃO consome cota
        using (var ack = await AgentClient.ReadAckAsync(
            await AgentClient.SendBatchAsync(client, deviceA.DeviceToken, firstBatch)))
        {
            Assert.Equal(0, ack.RootElement.GetProperty("accepted").GetInt32());
            Assert.Equal(5, ack.RootElement.GetProperty("duplicates").GetInt32());
        }

        // mais 5 aceitos → cota usada: 10/10 (só funciona se a duplicata devolveu os 5)
        var secondBatch = Enumerable.Range(0, 5).Select(_ => events.Event("HEARTBEAT", at)).ToList();
        using (var ack = await AgentClient.ReadAckAsync(
            await AgentClient.SendBatchAsync(client, deviceA.DeviceToken, secondBatch)))
        {
            Assert.Equal(5, ack.RootElement.GetProperty("accepted").GetInt32());
        }

        // 11º evento → 429 daily_quota_exceeded, Retry-After até a virada do dia UTC
        var exceeded = await AgentClient.SendBatchAsync(
            client, deviceA.DeviceToken, [events.Event("HEARTBEAT", at)]);
        await AssertProblem429Async(exceeded, "daily_quota_exceeded");
        Assert.InRange(RetryAfterSeconds(exceeded), 1, 86_400);

        // excedente NÃO persistido: exatamente os 10 aceitos no banco
        var persisted = await TestDb.ScalarAsync<long>(Cs,
            "SELECT count(*) FROM raw_events WHERE device_id = @d", ("d", deviceA.DeviceId));
        Assert.Equal(10, persisted);

        // lote VAZIO (keep-alive) passa mesmo com a cota esgotada
        var keepAlive = await AgentClient.SendBatchAsync(client, deviceA.DeviceToken, []);
        Assert.Equal(HttpStatusCode.OK, keepAlive.StatusCode);

        // a cota é POR DEVICE: outro device do MESMO tenant segue aceitando e persistindo
        var otherEvents = new EventFactory();
        using (var ack = await AgentClient.ReadAckAsync(
            await AgentClient.SendBatchAsync(client, deviceB.DeviceToken, [otherEvents.Event("HEARTBEAT", at)])))
        {
            Assert.Equal(1, ack.RootElement.GetProperty("accepted").GetInt32());
        }
    }

    // ----- desligado (default da suíte): nenhum 429 -----

    [Fact]
    public async Task RateLimitingDesabilitado_NaoLimitaEnrollNemIngestao()
    {
        // usa a PRÓPRIA fixture base (Enabled=false): rajada acima de todos os limites passa
        var client = fixture.CreateApiClient();
        var org = await fixture.CreateOrganizationAsync("Org RL Off");
        var (_, fullKey) = await fixture.CreateEnrollmentKeyWithSecretAsync(org.Id);
        var device = await AgentClient.EnrollAsync(client, fullKey);

        for (var i = 0; i < 5; i++)
        {
            var response = await AgentClient.SendBatchAsync(client, device.DeviceToken, []);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
