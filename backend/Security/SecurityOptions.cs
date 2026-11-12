// Validate security configuration before enabling the API; never log secret values.
using System.Text;
namespace UserManagement.Security;
public sealed record SecurityOptions(string JwtSecret, string Issuer, string Audience, byte[] IdentifierKey, byte[] BlindIndexKey)
{
    // Load required keys and reject defaults in every environment.
    // Raises: InvalidOperationException when operator configuration is missing or unsafe.
    public static SecurityOptions Load(IConfiguration config)
    {
        var secret = Required(config, "Jwt:Secret");
        if (Encoding.UTF8.GetByteCount(secret) < 32 || secret.Distinct().Count() < 10) throw new InvalidOperationException("Supply a random Jwt__Secret of at least 32 bytes.");
        if (config.GetValue<int>("Jwt:ExpiryMinutes") != 15) throw new InvalidOperationException("Jwt__ExpiryMinutes must be 15 under the approved policy.");
        var encryption = Key(config, "Security:IdentifierKey");
        var index = Key(config, "Security:BlindIndexKey");
        if (encryption.SequenceEqual(index)) throw new InvalidOperationException("Supply independent identifier and blind-index keys.");
        return new(secret, Required(config, "Jwt:Issuer"), Required(config, "Jwt:Audience"), encryption, index);
    }
    // Require non-placeholder configuration without echoing its value.
    private static string Required(IConfiguration config, string key)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value) || new[] { "change-me", "changeme", "dev-secret", "replace", "dummy", "example" }.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Supply non-default {key.Replace(":", "__")} configuration.");
        return value;
    }
    // Decode independent AES/HMAC keys and reject repeated-byte defaults.
    private static byte[] Key(IConfiguration config, string key)
    {
        try
        {
            var bytes = Convert.FromBase64String(Required(config, key));
            if (bytes.Length != 32 || bytes.Distinct().Count() < 10) throw new FormatException();
            return bytes;
        }
        catch (FormatException) { throw new InvalidOperationException($"Supply a random 32-byte base64 {key.Replace(":", "__")}."); }
    }
}
