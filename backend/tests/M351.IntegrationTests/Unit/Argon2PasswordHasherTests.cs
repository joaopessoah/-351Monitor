using M351.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace M351.IntegrationTests.Unit;

/// <summary>Testes unitários do hash Argon2id com os parâmetros CANÔNICOS (64 MB / 3 / 4 — Seção 7.5).</summary>
public class Argon2PasswordHasherTests
{
    private static Argon2PasswordHasher CreateCanonicalHasher() =>
        new(Options.Create(new PasswordHashingOptions()));

    [Fact]
    public void Hash_UsaFormatoPhc_ComParametrosCanonicos()
    {
        var hasher = CreateCanonicalHasher();
        var hash = hasher.Hash("senha-super-segura-123");

        // 64 MB = 65536 KB, 3 iterações, paralelismo 4 (lanes)
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=4$", hash);

        // salt de 16 bytes → 22 chars base64 sem padding
        var parts = hash.Split('$', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(22, parts[3].Length);
    }

    [Fact]
    public void Verify_SenhaCorreta_RetornaTrue()
    {
        var hasher = CreateCanonicalHasher();
        var hash = hasher.Hash("senha-super-segura-123");
        Assert.True(hasher.Verify("senha-super-segura-123", hash));
    }

    [Fact]
    public void Verify_SenhaErrada_RetornaFalse()
    {
        var hasher = CreateCanonicalHasher();
        var hash = hasher.Hash("senha-super-segura-123");
        Assert.False(hasher.Verify("senha-super-segura-124", hash));
    }

    [Fact]
    public void Hash_MesmaSenha_GeraSaltsEHashesDiferentes()
    {
        var hasher = CreateCanonicalHasher();
        var hash1 = hasher.Hash("senha-super-segura-123");
        var hash2 = hasher.Hash("senha-super-segura-123");
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Verify_HashMalformado_RetornaFalseSemLancar()
    {
        var hasher = CreateCanonicalHasher();
        Assert.False(hasher.Verify("qualquer", "nao-e-um-hash"));
        Assert.False(hasher.Verify("qualquer", "$bcrypt$nope"));
        Assert.False(hasher.Verify("qualquer", string.Empty));
    }

    [Fact]
    public void Verify_HashComParametrosDiferentes_ContinuaValidando()
    {
        // os parâmetros são lidos do próprio hash (PHC) — mudar a config não invalida hashes antigos
        var fast = new Argon2PasswordHasher(Options.Create(new PasswordHashingOptions
        {
            MemoryKb = 8192,
            Iterations = 1,
            Parallelism = 2,
        }));
        var canonical = CreateCanonicalHasher();

        var hash = fast.Hash("senha-super-segura-123");
        Assert.True(canonical.Verify("senha-super-segura-123", hash));
    }
}
