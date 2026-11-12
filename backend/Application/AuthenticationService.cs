// Verify credentials and await the durable login audit before returning success or denial.
using System.Text.RegularExpressions;
using UserManagement.Api;
using UserManagement.Audit;
using UserManagement.Domain;
using UserManagement.Infrastructure;
using UserManagement.Security;
namespace UserManagement.Application;
public interface IAuthenticationService { Task<LoginResult> SignInAsync(LoginRequest request, CancellationToken ct); }
public sealed class AuthenticationService(UserRepository users, AuditRepository audit, PasswordHasher hasher, ITokenService tokens) : IAuthenticationService
{
    // Equalize missing-account bcrypt work and never reveal which credential failed.
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)), 12);
    // Validate before lookup, verify even for inactive accounts, and omit an unknown actor.
    public async Task<LoginResult> SignInAsync(LoginRequest request, CancellationToken ct)
    {
        if (request.Username is null || !Regex.IsMatch(request.Username, "^[A-Za-z0-9_.-]{3,64}$"))
            throw ApiException.Invalid(new() { ["username"] = ["Use 3–64 letters, digits, dots, hyphens or underscores."] });
        PasswordHasher.Validate(request.Password);
        var user = await users.FindByUsernameAsync(request.Username, ct);
        var verified = hasher.Verify(request.Password!, user?.PasswordHash ?? DummyHash);
        var success = verified && user is { IsActive: true, CredentialVersion: > 0 } && Enum.IsDefined(user.Role);
        await audit.RecordAsync(AuditEvent.Create(user?.Id, "login", success), ct);
        if (!success) throw new ApiException(401, "authentication_failed", "Credentials could not be verified");
        return tokens.Issue(user!);
    }
}
public sealed record LoginRequest(string? Username, string? Password);
