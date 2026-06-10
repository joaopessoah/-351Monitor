using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace M351.IntegrationTests.Support;

public sealed record EnrolledDevice(Guid DeviceId, string DeviceToken, int ConfigVersion, JsonElement Config);

/// <summary>Helpers do lado do AGENTE contra a API real: enroll (Seção 5.7) e batch (Seção 5.4).</summary>
public static class AgentClient
{
    public static Task<HttpResponseMessage> EnrollRawAsync(
        HttpClient client, string enrollmentKey, string fingerprint,
        string hostname = "NB-TESTE", string agentVersion = "1.0.0") =>
        client.PostAsJsonAsync("/api/v1/agent/enroll", new Dictionary<string, object?>
        {
            ["enrollment_key"] = enrollmentKey,
            ["hostname"] = hostname,
            ["machine_fingerprint"] = fingerprint,
            ["os_version"] = "Windows 11 Pro 23H2 (22631)",
            ["agent_version"] = agentVersion,
        });

    public static async Task<EnrolledDevice> EnrollAsync(
        HttpClient client, string enrollmentKey, string? fingerprint = null, string hostname = "NB-TESTE")
    {
        var response = await EnrollRawAsync(client, enrollmentKey, fingerprint ?? NewFingerprint(), hostname);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created, $"Enroll falhou: {response.StatusCode} {body}");

        using var doc = JsonDocument.Parse(body);
        return new EnrolledDevice(
            doc.RootElement.GetProperty("device_id").GetGuid(),
            doc.RootElement.GetProperty("device_token").GetString()!,
            doc.RootElement.GetProperty("config_version").GetInt32(),
            doc.RootElement.GetProperty("config").Clone());
    }

    public static string NewFingerprint() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    /// <summary>POST /api/v1/ingest/batch com Authorization: Bearer (device token).</summary>
    public static async Task<HttpResponseMessage> SendBatchAsync(
        HttpClient client, string deviceToken, IEnumerable<Dictionary<string, object?>> events,
        int? configVersion = 1, DateTimeOffset? sentAt = null, string agentVersion = "1.0.0")
    {
        var body = new Dictionary<string, object?>
        {
            ["batch_id"] = Guid.NewGuid().ToString(),
            ["agent_version"] = agentVersion,
            ["sent_at"] = (sentAt ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("o"),
            ["config_version"] = configVersion,
            ["events"] = events.ToList(),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/batch")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        return await client.SendAsync(request);
    }

    public static async Task<JsonDocument> ReadAckAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Batch falhou: {response.StatusCode} {body}");
        return JsonDocument.Parse(body);
    }
}

/// <summary>Fábrica de eventos com envelope canônico (Seção 5.2) e seq monotônica por device.</summary>
public sealed class EventFactory(long startSeq = 1)
{
    public const string DefaultSid = "S-1-5-21-3623811015-3361044348-30300820-1013";
    public const string DefaultUser = "ACME\\maria.silva";

    private long _seq = startSeq;

    public string BootId { get; } = Guid.NewGuid().ToString();

    public long LastSeq => _seq - 1;

    public Dictionary<string, object?> Event(
        string type, DateTimeOffset? occurredAt = null, object? data = null,
        string? windowsSid = DefaultSid, string? windowsUser = DefaultUser, int? sessionId = 1)
    {
        var at = occurredAt ?? DateTimeOffset.UtcNow;
        return new Dictionary<string, object?>
        {
            ["event_id"] = Guid.NewGuid().ToString(),
            ["seq"] = _seq++,
            ["type"] = type,
            ["occurred_at"] = at.UtcDateTime.ToString("o"),
            ["tz_offset_min"] = -180,
            ["mono_ms"] = 86_400_000 + _seq * 1000,
            ["boot_id"] = BootId,
            ["session_id"] = sessionId,
            ["windows_sid"] = windowsSid,
            ["windows_user"] = windowsUser,
            ["data"] = data ?? new Dictionary<string, object?>(),
        };
    }
}

/// <summary>Consultas diretas ao banco de teste (verificação de persistência).</summary>
public static class TestDb
{
    public static async Task<T?> ScalarAsync<T>(string connectionString, string sql, params (string Name, object? Value)[] args)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = await command.ExecuteScalarAsync();
        return result switch
        {
            null or DBNull => default,
            T typed => typed,
            _ => (T)Convert.ChangeType(result, typeof(T))!,
        };
    }

    public static async Task<int> ExecuteAsync(string connectionString, string sql, params (string Name, object? Value)[] args)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>Lê uma linha como dicionário coluna→valor (ou null se não houver linha).</summary>
    public static async Task<Dictionary<string, object?>?> RowAsync(
        string connectionString, string sql, params (string Name, object? Value)[] args)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }

        return row;
    }
}
