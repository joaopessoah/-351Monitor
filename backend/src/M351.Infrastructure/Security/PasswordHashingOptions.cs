namespace M351.Infrastructure.Security;

/// <summary>
/// Parâmetros do Argon2id (Seção 7.5): 64 MB, 3 iterações, paralelismo 4, salt 16 bytes.
/// Configurável apenas para acelerar suítes de teste — produção usa os defaults canônicos.
/// </summary>
public class PasswordHashingOptions
{
    public const string SectionName = "PasswordHashing";

    public int MemoryKb { get; set; } = 64 * 1024;
    public int Iterations { get; set; } = 3;
    public int Parallelism { get; set; } = 4;
    public int SaltBytes { get; set; } = 16;
    public int HashBytes { get; set; } = 32;
}
