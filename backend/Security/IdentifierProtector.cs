// Encrypt sensitive identifiers and derive field-separated exact-match blind indexes.
using System.Security.Cryptography;
using System.Text;
namespace UserManagement.Security;
public sealed class IdentifierProtector(SecurityOptions options)
{
    // Encrypt with a fresh nonce and authenticate the field name to prevent substitution.
    // Returns: Versioned base64 nonce, tag, and ciphertext; no plaintext is persisted.
    public string Protect(string field, string value)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(options.IdentifierKey, 16);
        try { aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(field)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return "v1." + Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }
    // Authenticate and decrypt a stored identifier; reject tampering and wrong field context.
    public string Unprotect(string field, string value)
    {
        if (!value.StartsWith("v1.", StringComparison.Ordinal)) throw new CryptographicException("Unsupported protected value.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value[3..]); }
        catch (FormatException) { throw new CryptographicException("Invalid protected value."); }
        if (bytes.Length < 28) throw new CryptographicException("Invalid protected value.");
        var plain = new byte[bytes.Length - 28];
        using var aes = new AesGcm(options.IdentifierKey, 16);
        try { aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(field)); return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    // Compute exact-case equality without storing searchable plaintext.
    public string BlindIndex(string field, string value) => Convert.ToHexString(HMACSHA256.HashData(options.BlindIndexKey, Encoding.UTF8.GetBytes(field + "\0" + value)));
}
