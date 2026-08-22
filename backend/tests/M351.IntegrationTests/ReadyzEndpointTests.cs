using System.Net;
using System.Text.Json;
using M351.Domain;
using M351.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace M351.IntegrationTests;

/// <summary>
/// GET /readyz (F5, observabilidade): prontidão operacional além do /healthz. Responde 200
/// só quando a última execução com SUCESSO em maintenance_runs tem menos de 26 horas.
///
/// POR QUE ISTO PRECISA DE TESTE: o health-gate do deploy consulta este endpoint. Um /readyz
/// que responde 200 com o worker parado deixa passar um deploy quebrado; um que responde 503
/// com tudo saudável trava o deploy sem motivo.
///
/// maintenance_runs é uma tabela GLOBAL compartilhada por toda a coleção de testes, então o
/// cenário "manutenção velha" NÃO apaga linha nenhuma: adianta o relógio do host derivado
/// (TimeProvider), que é exatamente a variável que o endpoint compara.
/// </summary>
[Collection(ApiCollection.Name)]
public class ReadyzEndpointTests(ApiTestFixture fixture)
{
    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Planta uma execução ok recente (job real, como as demais suítes fazem).</summary>
    private async Task SeedManutencaoOkAsync(DateTimeOffset finishedAt) =>
        await TestDb.ExecuteAsync(fixture.Database.ConnectionString, """
            INSERT INTO maintenance_runs (id, job_name, started_at, finished_at, status, detail)
            VALUES (@id, 'RetentionPurge', @started, @finished, 'ok', '{}'::jsonb)
            """,
            ("id", Uuid7.NewUuid7()), ("started", finishedAt.AddMinutes(-1)), ("finished", finishedAt));

    [Fact]
    public async Task Readyz_ComManutencaoRecente_Retorna200Ready()
    {
        await SeedManutencaoOkAsync(DateTimeOffset.UtcNow);

        var client = fixture.CreateApiClient();
        var response = await client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ready", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readyz_UltimaManutencaoAlemDe26Horas_Retorna503()
    {
        // garante que existe manutenção ok (o cenário aqui é ela estar VELHA, não ausente)
        await SeedManutencaoOkAsync(DateTimeOffset.UtcNow);

        // host derivado com o relógio 48 h à frente: toda execução registrada vira antiga
        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<TimeProvider>(new FakeClock(DateTimeOffset.UtcNow.AddHours(48)))));

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var response = await client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // a resposta diz o que olhar (o limite e o worker), não um 503 mudo
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("26", raw);
        Assert.Contains("worker", raw, StringComparison.OrdinalIgnoreCase);

        // e o /healthz continua ok: banco de pé, prontidão operacional é outra pergunta
        var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}
