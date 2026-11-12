// Bound UTF-8 secrets before bcrypt to prevent silent truncation.
using System.Text;
using UserManagement.Api;
namespace UserManagement.Security;
public sealed class PasswordHasher
{
    // Hash a validated password using the approved work factor.
    public string Hash(string password) { Validate(password); return BCrypt.Net.BCrypt.HashPassword(password, 12); }
    // Compare a bounded secret; treat corrupt stored hashes as an authentication failure.
    public bool Verify(string password, string hash)
    {
        Validate(password);
        try { return BCrypt.Net.BCrypt.Verify(password, hash); }
        catch (BCrypt.Net.SaltParseException) { return false; }
    }
    // Reject missing or out-of-byte-range passwords without retaining their values.
    public static void Validate(string? password)
    {
        if (password is null || Encoding.UTF8.GetByteCount(password) is < 12 or > 72)
            throw ApiException.Invalid(new() { ["password"] = ["Use 12–72 UTF-8 bytes."] });
    }
}
