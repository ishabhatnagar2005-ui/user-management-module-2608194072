// Prove own-profile and credential invariants against isolated real Mongo snapshots.
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api;
using UserManagement.Application;
using UserManagement.Domain;
using UserManagement.Infrastructure;
using UserManagement.Security;
using Xunit;
namespace UserManagement.Tests;
public sealed class ProfileTests
{
    // Deserialize absent-versus-null semantics through the actual request contract.
    private static ProfileUpdateRequest Patch(string json) => JsonSerializer.Deserialize<ProfileUpdateRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    // Verify both roles read and mutate only their own account, retaining every protected field.
    [Theory] [InlineData("test.user")] [InlineData("test.admin")]
    public async Task Four_fields_update_preserves_credentials_identity_and_other_account(string username)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10";
        var repo = app.Service<UserRepository>(); var service = app.Service<ProfileService>();
        var current = (await repo.FindByUsernameAsync(username, default))!;
        var other = (await repo.FindByUsernameAsync(username == "test.user" ? "test.admin" : "test.user", default))!;
        var read = await service.ReadAsync(current.Id, default); Assert.Equal(current.Id.ToString(), read.Id);
        var result = await service.UpdateAsync(current.Id, Patch("""{"address":"Synthetic address","city":"Synthetic city","country":"Country","education":"Education"}"""), default);
        var stored = (await repo.FindByIdAsync(current.Id, default))!;
        Assert.Equal("Synthetic address", result.Address); Assert.Equal("Synthetic city", stored.City); Assert.Equal("Country", stored.Country); Assert.Equal("Education", stored.Education);
        Assert.True(stored.UpdatedAt > current.UpdatedAt); Assert.Equal(DateTimeKind.Utc, stored.UpdatedAt.Kind); Assert.Equal(0, stored.UpdatedAt.Ticks % TimeSpan.TicksPerMillisecond);
        var before = current.ToBsonDocument(); var after = stored.ToBsonDocument(); foreach (var field in new[] { "address", "city", "country", "education", "updated_at" }) { before.Remove(field); after.Remove(field); } Assert.Equal(before, after);
        Assert.Equal(other.ToBsonDocument(), (await repo.FindByIdAsync(other.Id, default))!.ToBsonDocument());
        await service.UpdateAsync(current.Id, Patch("""{"address":null}"""), default);
        var cleared = (await repo.FindByIdAsync(current.Id, default))!; Assert.Null(cleared.Address); Assert.Equal(stored.City, cleared.City);
        var logs = await app.Service<MongoStore>().Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument("action", "profile.update")).ToListAsync(); Assert.Equal(2, logs.Count);
        foreach (var log in logs) { Assert.Equal(new[] { "_id", "action", "status", "timestamp", "user_id" }, log.Names.Order().ToArray()); Assert.Equal(current.Id, log["user_id"].AsObjectId); Assert.Equal("success", log["status"]); Assert.DoesNotContain("Synthetic", log.ToJson()); }
    }
    // Reject every optional bound, hostile type and control character before any partial write.
    [Theory] [InlineData("address", 501)] [InlineData("city", 101)] [InlineData("country", 101)] [InlineData("education", 201)]
    public async Task Oversized_field_preserves_entire_document(string field, int size)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10";
        var repo = app.Service<UserRepository>(); var current = (await repo.FindByUsernameAsync("test.user", default))!;
        var error = await Assert.ThrowsAsync<ApiException>(() => app.Service<ProfileService>().UpdateAsync(current.Id,
            Patch(JsonSerializer.Serialize(new Dictionary<string, string> { ["address"] = "No partial write", [field] = new string('a', size) })), default));
        Assert.Equal(400, error.Status); Assert.Contains(field, error.Errors.Keys);
        Assert.Equal(current.ToBsonDocument(), (await repo.FindByIdAsync(current.Id, default))!.ToBsonDocument());
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "profile.update" && x.Status == "failure"));
    }
    // Exercise nontext inputs and disallowed control characters.
    [Theory] [InlineData("""{"city":{"$ne":null}}""")] [InlineData("""{"country":true}""")] [InlineData("""{"education":12}""")] [InlineData("""{"address":"bad\ntext"}""")]
    public async Task Hostile_field_is_inert(string json)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10";
        var repo = app.Service<UserRepository>(); var current = (await repo.FindByUsernameAsync("test.user", default))!;
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => app.Service<ProfileService>().UpdateAsync(current.Id, Patch(json), default))).Status);
        Assert.Equal(current.ToBsonDocument(), (await repo.FindByIdAsync(current.Id, default))!.ToBsonDocument());
    }
    // Accept inclusive byte bounds, preserve profile/secrets and increment credentials exactly once.
    [Theory] [InlineData(12, false)] [InlineData(72, false)] [InlineData(36, true)]
    public async Task Password_change_persists_cost12_and_preserves_all_other_fields(int length, bool unicode)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10";
        var repo = app.Service<UserRepository>(); var current = (await repo.FindByUsernameAsync("test.user", default))!;
        var next = new string(unicode ? 'é' : 'a', length);
        await app.Service<ProfileService>().ChangePasswordAsync(current.Id, new(app.Password, next), default);
        var stored = (await repo.FindByIdAsync(current.Id, default))!;
        Assert.StartsWith("$2a$12$", stored.PasswordHash); Assert.NotEqual(current.PasswordHash, stored.PasswordHash);
        Assert.True(app.Service<PasswordHasher>().Verify(next, stored.PasswordHash)); Assert.False(app.Service<PasswordHasher>().Verify(app.Password, stored.PasswordHash)); Assert.Equal(current.CredentialVersion + 1, stored.CredentialVersion);
        Assert.True(stored.UpdatedAt > current.UpdatedAt);
        var before = current.ToBsonDocument(); var after = stored.ToBsonDocument(); foreach (var field in new[] { "password", "credential_version", "updated_at" }) { before.Remove(field); after.Remove(field); } Assert.Equal(before, after);
        var log = await app.Service<MongoStore>().Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument("action", "password.change")).SingleAsync(); Assert.Equal("success", log["status"]);
        Assert.Equal(new[] { "_id", "action", "status", "timestamp", "user_id" }, log.Names.Order().ToArray()); Assert.DoesNotContain(app.Password, log.ToJson()); Assert.DoesNotContain(next, log.ToJson());
    }
    // Invalid byte lengths, missing secrets and wrong current proof retain the old hash and version.
    [Theory] [InlineData(null, 20, false)] [InlineData("short", 20, false)] [InlineData("wrong-current-password", 20, false)]
    [InlineData("valid", 0, false)] [InlineData("valid", 11, false)] [InlineData("valid", 73, false)] [InlineData("valid", 37, true)] [InlineData("valid", -1, false)]
    public async Task Invalid_password_change_retains_old_credential(string? currentPassword, int length, bool unicode)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10";
        var repo = app.Service<UserRepository>(); var current = (await repo.FindByUsernameAsync("test.user", default))!;
        var request = new PasswordChangeRequest(currentPassword == "valid" ? app.Password : currentPassword, length < 0 ? null : new string(unicode ? 'é' : 'a', length));
        var error = await Assert.ThrowsAsync<ApiException>(() => app.Service<ProfileService>().ChangePasswordAsync(current.Id, request, default)); Assert.Equal(400, error.Status);
        Assert.Equal(current.ToBsonDocument(), (await repo.FindByIdAsync(current.Id, default))!.ToBsonDocument()); Assert.True(app.Service<PasswordHasher>().Verify(app.Password, current.PasswordHash));
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "password.change" && x.Status == "failure"));
    }
    // Reject removed and inactive principals for read/edit/password even outside the HTTP gate.
    [Theory] [InlineData(false, "read")] [InlineData(false, "update")] [InlineData(false, "password")]
    [InlineData(true, "read")] [InlineData(true, "update")] [InlineData(true, "password")]
    public async Task Missing_or_inactive_principal_is_denied(bool inactive, string operation)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10";
        var store = app.Service<MongoStore>(); var current = (await app.Service<UserRepository>().FindByUsernameAsync("test.user", default))!;
        if (inactive) await store.Users.UpdateOneAsync(x => x.Id == current.Id, Builders<UserRecord>.Update.Set(x => x.IsActive, false));
        var id = inactive ? current.Id : ObjectId.GenerateNewId(); var service = app.Service<ProfileService>();
        var error = await Assert.ThrowsAsync<ApiException>(async () => { if (operation == "read") await service.ReadAsync(id, default); else if (operation == "update") await service.UpdateAsync(id, Patch("{}"), default); else await service.ChangePasswordAsync(id, new(app.Password, "new-valid-password"), default); }); Assert.Equal(401, error.Status);
        Assert.Equal(current.PasswordHash, (await app.Service<UserRepository>().FindByIdAsync(current.Id, default))!.PasswordHash);
    }
    // Force real audit rejection and prove the user write and success event roll back atomically.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Success_audit_failure_rolls_back_profile_or_password(bool password)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10";
        var store = app.Service<MongoStore>(); var current = (await app.Service<UserRepository>().FindByUsernameAsync("test.user", default))!;
        await store.Database.CreateCollectionAsync("logs"); await store.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "logs" }, { "validator", new BsonDocument("status", "failure") }, { "validationLevel", "strict" } });
        var service = app.Service<ProfileService>(); await Assert.ThrowsAnyAsync<MongoException>(async () => { if (password) await service.ChangePasswordAsync(current.Id, new(app.Password, "new-valid-password"), default); else await service.UpdateAsync(current.Id, Patch("""{"city":"Rollback"}"""), default); });
        Assert.Equal(current.ToBsonDocument(), (await app.Service<UserRepository>().FindByIdAsync(current.Id, default))!.ToBsonDocument());
        Assert.Equal(1, await store.Logs.CountDocumentsAsync(x => x.Status == "failure")); Assert.Equal(0, await store.Logs.CountDocumentsAsync(x => x.Status == "success"));
    }
    // Retain actual encrypted identifiers and populated profile fields across both approved mutations.
    [Fact]
    public async Task Populated_sensitive_fields_remain_byte_for_byte_unchanged()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10";
        var created = await app.Service<UserService>().CreateAsync(new("profile.populated", app.Password, "Address", "City", "Country", "Education", "SYNTHETIC-A", "SYNTHETIC-P"), ObjectId.GenerateNewId(), default);
        var repo = app.Service<UserRepository>(); var id = ObjectId.Parse(created.Id); var before = (await repo.FindByIdAsync(id, default))!;
        await app.Service<ProfileService>().UpdateAsync(id, Patch("""{"city":"Updated city"}"""), default);
        await app.Service<ProfileService>().ChangePasswordAsync(id, new(app.Password, "new-valid-password"), default);
        var after = (await repo.FindByIdAsync(id, default))!;
        Assert.Equal(before.AadhaarNo, after.AadhaarNo); Assert.Equal(before.PanNo, after.PanNo); Assert.Equal(before.AadhaarIndex, after.AadhaarIndex); Assert.Equal(before.PanIndex, after.PanIndex);
        Assert.NotNull(after.AadhaarNo); Assert.NotNull(after.PanNo); Assert.Equal(before.Address, after.Address); Assert.Equal(before.Country, after.Country); Assert.Equal(before.Education, after.Education); Assert.Equal(before.Username, after.Username); Assert.Equal("Updated city", after.City);
    }
    // A stale version cannot write even when supplied directly to the real conditional repository.
    [Fact]
    public async Task Conditional_repository_rejects_stale_version_without_any_change()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10";
        var repo = app.Service<UserRepository>(); var current = (await repo.FindByUsernameAsync("test.user", default))!;
        var result = await app.Service<MongoStore>().TransactionAsync((session, ct) => repo.UpdateCredentialConditionalAsync(current.Id, current.CredentialVersion - 1, Builders<UserRecord>.Update.Set(x => x.PasswordHash, "must-not-write"), session, ct), default);
        Assert.Null(result); Assert.Equal(current.ToBsonDocument(), (await repo.FindByIdAsync(current.Id, default))!.ToBsonDocument());
    }
    // Concurrent current-password proofs must have one winner; the retry re-verifies the fresh hash.
    [Fact]
    public async Task Concurrent_password_changes_have_one_winner_and_one_failure()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10";
        var service = app.Service<ProfileService>(); var repo = app.Service<UserRepository>(); var current = (await repo.FindByUsernameAsync("test.user", default))!;
        var attempts = await Task.WhenAll(new[] { "new-password-one", "new-password-two" }.Select(async next => { try { await service.ChangePasswordAsync(current.Id, new(app.Password, next), default); return true; } catch (ApiException error) { Assert.Equal(400, error.Status); return false; } }));
        Assert.Single(attempts, x => x); var stored = (await repo.FindByIdAsync(current.Id, default))!; Assert.Equal(current.CredentialVersion + 1, stored.CredentialVersion);
        Assert.True(app.Service<PasswordHasher>().Verify("new-password-one", stored.PasswordHash) ^ app.Service<PasswordHasher>().Verify("new-password-two", stored.PasswordHash));
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "password.change" && x.Status == "success")); Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "password.change" && x.Status == "failure"));
    }
}
