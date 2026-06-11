using System.Security.Cryptography;

namespace M351.Domain;

/// <summary>
/// Gerador de UUID v7 (RFC 9562): 48 bits de timestamp Unix em ms + bits aleatórios.
/// IDs expostos pelo sistema são sempre UUIDv7 (ordenáveis por tempo).
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

    /// <summary>
    /// Extrai o instante de criação (48 bits de Unix ms) de um UUIDv7 gerado por este sistema.
    /// Usado onde não há coluna de timestamp própria — ex.: enrolled_at de devices (F3.7), que
    /// é o instante do PRIMEIRO enroll (re-enroll por fingerprint preserva o id).
    /// </summary>
    public static DateTimeOffset TimestampOf(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);

        long unixMs = ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24)
                    | ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
    }
}
