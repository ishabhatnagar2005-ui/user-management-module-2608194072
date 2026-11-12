// Exercise authentication services and security primitives against isolated real Mongo state.
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api;
using UserManagement.Application;
using UserManagement.Audit;
using UserManagement.Domain;
using UserManagement.Infrastructure;
using UserManagement.Security;
using Xunit;
namespace UserManagement.Tests;
public sealed class AuthenticationTests
{
    [Fact]
    public async Task Valid_login_issues_configured_token_and_majority_audit()
    {
        await using var app = new TestApplication();
        var result = await app.Service<IAuthenticationService>().SignInAsync(new("test.admin", app.Password), default);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);
        Assert.Equal("HS256", token.Header.Alg); Assert.Equal("test-api", token.Issuer); Assert.Contains("test-client", token.Audiences);
        Assert.Equal("Admin", token.Claims.Single(c => c.Type == "role").Value);
        Assert.Equal(TimeSpan.FromMinutes(15), token.ValidTo - token.ValidFrom);
        new JwtSecurityTokenHandler().ValidateToken(result.AccessToken, app.Service<TokenService>().ValidationParameters(), out _);
        var user = await app.Service<UserRepository>().FindByUsernameAsync("test.admin", default);
        Assert.NotNull(user); Assert.NotEqual(app.Password, user.PasswordHash); Assert.StartsWith("$2", user.PasswordHash);
        Assert.True(app.Service<PasswordHasher>().Verify(app.Password, user.PasswordHash));
        var log = Assert.Single(await app.Service<AuditRepository>().ReadAsync(FilterDefinition<AuditEvent>.Empty, 100, default));
        Assert.Equal(user.Id, log.UserId); Assert.Equal("login", log.Action); Assert.Equal("success", log.Status); Assert.Equal(DateTimeKind.Utc, log.Timestamp.Kind);
        var raw = await app.Service<MongoStore>().Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(new[] { "_id", "action", "status", "timestamp", "user_id" }, raw.Names.Order().ToArray());
        Assert.DoesNotContain(app.Password, raw.ToJson()); Assert.DoesNotContain(result.AccessToken, raw.ToJson());
    }
    [Theory]
    [InlineData("missing.account", false)]
    [InlineData("test.user", false)]
    [InlineData("test.user", true)]
    public async Task Missing_bad_password_and_inactive_accounts_share_denial_with_durable_failure(string username, bool deactivate)
    {
        await using var app = new TestApplication();
        var service = app.Service<IAuthenticationService>();
        if (deactivate) await app.Service<MongoStore>().Users.UpdateOneAsync(x => x.Username == username, Builders<UserRecord>.Update.Set(x => x.IsActive, false));
        var error = await Assert.ThrowsAsync<ApiException>(() => service.SignInAsync(new(username, deactivate ? app.Password : "incorrect-secret"), default));
        Assert.Equal(401, error.Status); Assert.Equal("authentication_failed", error.Type); Assert.Equal("Credentials could not be verified", error.Message); Assert.Empty(error.Errors);
        var raw = await app.Service<MongoStore>().Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument()).SingleAsync();
        Assert.Equal("failure", raw["status"].AsString); Assert.Equal(username != "missing.account", raw.Contains("user_id"));
        Assert.DoesNotContain(username, raw.ToJson()); Assert.DoesNotContain("incorrect-secret", raw.ToJson());
    }
    [Theory]
    [InlineData(null, "valid-password")]
    [InlineData("ab", "valid-password")]
    [InlineData("<script>", "valid-password")]
    [InlineData("valid.user", null)]
    [InlineData("valid.user", "short")]
    public async Task Invalid_credentials_return_validation_before_audit(string? username, string? password)
    {
        await using var app = new TestApplication();
        var error = await Assert.ThrowsAsync<ApiException>(() => app.Service<IAuthenticationService>().SignInAsync(new(username, password), default));
        Assert.Equal(400, error.Status);
        Assert.Equal(0, await app.Service<MongoStore>().Logs.CountDocumentsAsync(FilterDefinition<AuditEvent>.Empty));
    }
    [Fact]
    public void Password_byte_limits_accept_edges_reject_utf8_overflow_and_corrupt_hash()
    {
        var hasher = new PasswordHasher();
        foreach (var password in new[] { new string('a', 12), new string('b', 72), new string('é', 36) })
        { var hash = hasher.Hash(password); Assert.True(hasher.Verify(password, hash)); Assert.False(hasher.Verify("wrong-password", hash)); Assert.Equal("12", BCrypt.Net.BCrypt.InterrogateHash(hash).WorkFactor); }
        Assert.Throws<ApiException>(() => hasher.Hash(new string('a', 11)));
        Assert.Throws<ApiException>(() => hasher.Hash(new string('a', 73)));
        Assert.Throws<ApiException>(() => hasher.Hash(new string('é', 37)));
        Assert.False(hasher.Verify("valid-password", "invalid-hash"));
    }
    [Fact]
    public async Task Identifier_roundtrip_random_ciphertext_exact_index_and_tamper_rejection()
    {
        await using var app = new TestApplication(); var protector = app.Service<IdentifierProtector>();
        var value = "SYNTHETIC-ONLY-1234"; var encrypted = protector.Protect("pan_no", value);
        Assert.Equal(value, protector.Unprotect("pan_no", encrypted)); Assert.NotEqual(encrypted, protector.Protect("pan_no", value)); Assert.DoesNotContain(value, encrypted);
        Assert.Equal(protector.BlindIndex("pan_no", value), protector.BlindIndex("pan_no", value));
        Assert.NotEqual(protector.BlindIndex("pan_no", value), protector.BlindIndex("aadhaar_no", value));
        Assert.NotEqual(protector.BlindIndex("pan_no", value), protector.BlindIndex("pan_no", value.ToLowerInvariant()));
        var bytes = Convert.FromBase64String(encrypted[3..]); bytes[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("pan_no", "v1." + Convert.ToBase64String(bytes)));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("aadhaar_no", encrypted));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("pan_no", "v2.invalid"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("pan_no", "v1.invalid"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("pan_no", "v1.AA=="));
    }
    [Fact]
    public async Task Seed_and_index_are_idempotent_and_duplicate_race_has_one_winner()
    {
        await using var app = new TestApplication(); var store = app.Service<MongoStore>();
        var before = await app.Service<UserRepository>().FindByUsernameAsync("test.admin", default);
        await app.Service<DevelopmentSeed>().ApplyAsync(default); await app.Service<IndexMigration>().ApplyAsync(default);
        var after = await app.Service<UserRepository>().FindByUsernameAsync("test.admin", default);
        Assert.Equal(before!.Id, after!.Id); Assert.Equal(before.PasswordHash, after.PasswordHash); Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(2, await store.Users.CountDocumentsAsync(FilterDefinition<UserRecord>.Empty));
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        { try { await app.Service<UserRepository>().InsertAsync(new() { Username = "race.user", PasswordHash = before.PasswordHash }, default); return true; } catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey) { return false; } }));
        Assert.Single(results, x => x); Assert.Equal(1, await store.Users.CountDocumentsAsync(x => x.Username == "race.user"));
        await app.Service<UserRepository>().InsertAsync(new() { Username = "Race.user", PasswordHash = before.PasswordHash }, default);
        Assert.Equal(1, await store.Users.CountDocumentsAsync(x => x.Username == "Race.user"));
    }
    [Fact]
    public async Task Transaction_commits_both_documents_and_rolls_back_on_failure()
    {
        await using var app = new TestApplication(); var store = app.Service<MongoStore>(); var users = app.Service<UserRepository>(); var audit = app.Service<AuditRepository>();
        var account = new UserRecord { Username = "atomic.user", PasswordHash = app.Service<PasswordHasher>().Hash(app.Password) };
        await store.TransactionAsync(async (session, ct) => { await users.InsertAsync(account, ct, session); await audit.RecordAsync(AuditEvent.Create(account.Id, "user.create", true), ct, session); return true; }, default);
        Assert.NotNull(await users.FindByIdAsync(account.Id, default)); Assert.Equal(1, await store.Logs.CountDocumentsAsync(x => x.UserId == account.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TransactionAsync<bool>(async (session, ct) => { await users.InsertAsync(new() { Username = "rollback.user" }, ct, session); throw new InvalidOperationException("controlled"); }, default));
        Assert.Null(await users.FindByUsernameAsync("rollback.user", default));
    }
    [Theory]
    [InlineData("Jwt:Secret", "dev-secret-change-me")]
    [InlineData("Jwt:Issuer", "")]
    [InlineData("Jwt:Audience", "")]
    [InlineData("Security:IdentifierKey", "invalid")]
    [InlineData("Security:BlindIndexKey", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("Jwt:ExpiryMinutes", "30")]
    public async Task Unsafe_security_configuration_fails_closed(string key, string value)
    {
        await using var app = new TestApplication(); var values = new Dictionary<string, string?>(app.Settings) { [key] = value };
        Assert.Throws<InvalidOperationException>(() => SecurityOptions.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
    }
    [Fact]
    public async Task Audit_failure_cannot_return_success()
    {
        await using var app = new TestApplication(); app.Service<MongoStore>();
        var offline = new MongoStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(app.Settings) { ["Mongo:Uri"] = "mongodb://127.0.0.1:1/" }).Build());
        var service = new AuthenticationService(app.Service<UserRepository>(), new AuditRepository(offline), app.Service<PasswordHasher>(), app.Service<ITokenService>());
        await Assert.ThrowsAsync<TimeoutException>(() => service.SignInAsync(new("test.admin", app.Password), default));
        Assert.Equal(0, await app.Service<MongoStore>().Logs.CountDocumentsAsync(FilterDefinition<AuditEvent>.Empty));
    }
}
