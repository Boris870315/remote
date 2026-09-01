namespace Remote.Infrastructure.Security;

/// <summary>Versioned parameters for deriving and using a Workspace encryption key.</summary>
public sealed record WorkspaceEncryptionOptions
{
    public const int CurrentFormatVersion = 1;

    public int Pbkdf2Iterations { get; init; } = 600_000;

    public int SaltSize { get; init; } = 16;

    public int NonceSize { get; init; } = 12;

    public int TagSize { get; init; } = 16;

    public int KeySize { get; init; } = 32;
}
