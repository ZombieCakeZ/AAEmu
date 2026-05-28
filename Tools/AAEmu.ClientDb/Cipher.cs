namespace AAEmu.ClientDb;

/// <summary>
/// AA client compact.sqlite cipher abstraction. Each known version supplies
/// a concrete implementation; <see cref="ResolveOrThrow"/> picks the right
/// one by version string.
///
/// As of writing none of the ciphers are implemented because the keys live
/// in a packed archeage.exe — see Docs/Decryptor_RE_Status.md for the
/// reverse-engineering plan.
/// </summary>
public interface ICipher
{
    string Version { get; }

    /// <summary>Decrypt the whole file in place. Implementations may operate page-by-page.</summary>
    void DecryptInPlace(Span<byte> data);

    /// <summary>Encrypt the whole file in place. Inverse of <see cref="DecryptInPlace"/>.</summary>
    void EncryptInPlace(Span<byte> data);
}

public static class CipherRegistry
{
    private static readonly Dictionary<string, Func<ICipher>> _factories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1_2"] = () => throw new NotImplementedException("1.2 cipher not yet wired (Phase 2.2)."),
        ["2_0_1_7"] = () => throw new NotImplementedException("2.0.1.7 cipher not yet wired (Phase 2.1 — RE blocked on packed binary)."),
    };

    public static IReadOnlyCollection<string> KnownVersions => _factories.Keys;

    public static ICipher ResolveOrThrow(string version)
    {
        if (!_factories.TryGetValue(version, out var factory))
        {
            throw new ArgumentException(
                $"unknown cipher version '{version}'. Known: {string.Join(", ", _factories.Keys)}");
        }

        return factory();
    }
}
