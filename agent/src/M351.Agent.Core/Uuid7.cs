using System.Security.Cryptography;

namespace M351.Agent.Core;

/// <summary>
/// Gerador de UUID v7 (RFC 9562): 48 bits de timestamp Unix em ms + bits aleatórios.
/// `event_id` do envelope canônico (Seção 5.2) é sempre UUIDv7 gerado no agente.
/// (Cópia do utilitário de backend/src/M351.Domain/Uuid7.cs — manter idêntico.)
/// </summary>
public static class Uuid7
{
    public static Guid NewUuid7() => NewUuid7(DateTimeOffset.UtcNow);

    public static Guid NewUuid7(DateTimeOffset timestamp)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);

        long unixMs = timestamp.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(unixMs >> 40);
        bytes[1] = (byte)(unixMs >> 32);
        bytes[2] = (byte)(unixMs >> 24);
        bytes[3] = (byte)(unixMs >> 16);
        bytes[4] = (byte)(unixMs >> 8);
        bytes[5] = (byte)unixMs;

        // versão 7 (bits altos do byte 6) e variante RFC 4122 (bits altos do byte 8)
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        // Guid(byte[]) usa layout little-endian nos 3 primeiros grupos; usar o construtor big-endian
        return new Guid(bytes, bigEndian: true);
    }
}
