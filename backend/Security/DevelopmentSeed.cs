// Seed only explicitly enabled development/test accounts from operator configuration.
using System.Text.RegularExpressions;
using UserManagement.Domain;
using UserManagement.Infrastructure;
namespace UserManagement.Security;
public sealed class DevelopmentSeed(IConfiguration config, IWebHostEnvironment environment, UserRepository users, PasswordHasher hasher)
{
    // Preserve existing accounts across restarts; reject production seeding and invalid inputs.
    public async Task ApplyAsync(CancellationToken ct)
    {
        if (!config.GetValue<bool>("Seed:Enabled")) return;
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing")) throw new InvalidOperationException("Seed__Enabled is forbidden in production.");
        var admin = Account("Admin", UserRole.Admin);
        var user = Account("User", UserRole.User);
        if (admin.Username == user.Username) throw new InvalidOperationException("Seed usernames must differ.");
        foreach (var account in new[] { admin, user })
        {
            account.PasswordHash = hasher.Hash(config[$"Seed:{account.Role}Password"]!);
            await users.SeedAsync(account, ct);
        }
    }
    // Build a synthetic account after validating the configured credential shape.
    private UserRecord Account(string prefix, UserRole role)
    {
        var username = config[$"Seed:{prefix}Username"];
        if (username is null || !Regex.IsMatch(username, "^[A-Za-z0-9_.-]{3,64}$")) throw new InvalidOperationException($"Supply Seed__{prefix}Username.");
        PasswordHasher.Validate(config[$"Seed:{prefix}Password"]);
        return new UserRecord { Username = username, Role = role };
    }
}
