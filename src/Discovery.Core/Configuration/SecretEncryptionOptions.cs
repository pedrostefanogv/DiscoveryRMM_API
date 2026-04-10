namespace Discovery.Core.Configuration;

public class SecretEncryptionOptions
{
    public const string SectionName = "Security:Encryption";

    public bool Enabled { get; set; } = false;

    public string KeyId { get; set; } = "v1";

    public string? MasterKeyBase64 { get; set; }
}
