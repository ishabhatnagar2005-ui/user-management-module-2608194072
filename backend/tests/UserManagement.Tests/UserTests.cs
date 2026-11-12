// Verify account service branches against isolated real Mongo databases.
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
// Prove state transitions, safe projection, byte bounds and transaction rollback.
public sealed class UserTests
{
    // Requery the persisted account and audit rather than trusting the returned DTO.
    [Fact]
    public async Task Create_persists_hash_encryption_defaults_and_safe_read_without_mutation()
    {
        await using var app = new TestApplication();
        var service = app.Service<UserService>();
        var actor = (await app.Service<UserRepository>().FindByUsernameAsync("test.admin", default))!.Id;
        var request = new CreateUserRequest("created.user", app.Password, "Synthetic address", "Test city", "Test country", "Test education", "SYNTHETIC-AADHAAR", "SYNTHETIC-PAN");
        var created = await service.CreateAsync(request, actor, default);
        var stored = await app.Service<UserRepository>().FindByIdAsync(ObjectId.Parse(created.Id), default);
        Assert.NotNull(stored); Assert.Equal(request.Username, stored.Username); Assert.Equal(request.Address, stored.Address);
        Assert.Equal(request.City, stored.City); Assert.Equal(request.Country, stored.Country); Assert.Equal(request.Education, stored.Education);
        Assert.True(app.Service<PasswordHasher>().Verify(app.Password, stored.PasswordHash));
        Assert.Equal("12", BCrypt.Net.BCrypt.InterrogateHash(stored.PasswordHash).WorkFactor);
        Assert.Equal(UserRole.User, stored.Role); Assert.Equal(1, stored.CredentialVersion); Assert.True(stored.IsActive);
        Assert.Equal(DateTimeKind.Utc, stored.CreatedAt.Kind); Assert.Equal(stored.CreatedAt, stored.UpdatedAt); Assert.Equal(0, stored.CreatedAt.Ticks % TimeSpan.TicksPerMillisecond);
        var protector = app.Service<IdentifierProtector>();
        Assert.Equal(request.AadhaarNo, protector.Unprotect("aadhaar_no", stored.AadhaarNo!));
        Assert.Equal(request.PanNo, protector.Unprotect("pan_no", stored.PanNo!));
        Assert.Equal(protector.BlindIndex("aadhaar_no", request.AadhaarNo!), stored.AadhaarIndex);
        Assert.Equal(protector.BlindIndex("pan_no", request.PanNo!), stored.PanIndex);
        var before = stored.ToBsonDocument(); var read = await service.ReadAsync(created.Id, actor, default);
        Assert.Equal(created, read); Assert.Equal(before, (await app.Service<UserRepository>().FindByIdAsync(stored.Id, default))!.ToBsonDocument());
        var logs = await app.Service<MongoStore>().Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument()).ToListAsync();
        Assert.Equal(2, logs.Count);
        foreach (var log in logs) { Assert.Equal(new[] { "_id", "action", "status", "timestamp", "user_id" }, log.Names.Order().ToArray()); Assert.Equal(actor, log["user_id"].AsObjectId); Assert.Equal("success", log["status"].AsString); Assert.DoesNotContain(request.Username!, log.ToJson()); Assert.DoesNotContain(app.Password, log.ToJson()); }
        Assert.Contains(logs, x => x["action"] == "user.create"); Assert.Contains(logs, x => x["action"] == "user.read");
    }
    // Accept exact-case distinct usernames, omitted identifiers, and approved size boundaries.
    [Fact]
    public async Task Create_accepts_boundaries_and_case_distinction_with_optional_fields_absent()
    {
        await using var app = new TestApplication(); var service = app.Service<UserService>(); var actor = ObjectId.GenerateNewId();
        foreach (var request in new[] { new CreateUserRequest("abc", new string('a', 12)), new CreateUserRequest("Abc", new string('é', 36)),
            new CreateUserRequest(new string('a', 64), new string('b', 72), new string('a', 500), new string('c', 100), new string('c', 100), new string('e', 200), new string('i', 64), new string('p', 64)) })
        { var result = await service.CreateAsync(request, actor, default); Assert.Equal(request.Username, (await app.Service<UserRepository>().FindByIdAsync(ObjectId.Parse(result.Id), default))!.Username); }
        var empty = (await app.Service<UserRepository>().FindByUsernameAsync("abc", default))!;
        Assert.Null(empty.AadhaarNo); Assert.Null(empty.PanNo); Assert.Null(empty.AadhaarIndex); Assert.Null(empty.PanIndex);
        Assert.Equal(5, await app.Service<MongoStore>().Users.CountDocumentsAsync(FilterDefinition<UserRecord>.Empty));
    }
    // Reject every input boundary before a user insert and retain a redacted failure event.
    [Theory]
    [InlineData("username", 2)] [InlineData("username", 65)] [InlineData("username", -1)]
    [InlineData("password", 11)] [InlineData("password", 73)] [InlineData("utf8", 37)]
    [InlineData("address", 501)] [InlineData("city", 101)] [InlineData("country", 101)] [InlineData("education", 201)]
    [InlineData("aadhaar_no", 65)] [InlineData("pan_no", 65)] [InlineData("control", 1)]
    public async Task Invalid_create_has_no_user_write_and_one_failure(string field, int length)
    {
        await using var app = new TestApplication(); var request = new CreateUserRequest("invalid.user", app.Password);
        var value = length >= 0 ? new string('a', length) : "bad.user\n";
        request = field switch { "username" => request with { Username = value }, "password" => request with { Password = value }, "utf8" => request with { Password = new string('é', length) },
            "address" => request with { Address = value }, "city" => request with { City = value }, "country" => request with { Country = value }, "education" => request with { Education = value },
            "aadhaar_no" => request with { AadhaarNo = value }, "pan_no" => request with { PanNo = value }, _ => request with { City = "bad\ncity" } };
        var error = await Assert.ThrowsAsync<ApiException>(() => app.Service<UserService>().CreateAsync(request, ObjectId.GenerateNewId(), default));
        Assert.Equal(400, error.Status); Assert.Equal(2, await app.Service<MongoStore>().Users.CountDocumentsAsync(FilterDefinition<UserRecord>.Empty));
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "user.create" && x.Status == "failure"));
    }
    // Exercise the database unique constraint under concurrent service transactions.
    [Fact]
    public async Task Concurrent_duplicate_has_one_winner_and_one_conflict_without_partial_write()
    {
        await using var app = new TestApplication(); var service = app.Service<UserService>(); var actor = ObjectId.GenerateNewId();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ => { try { await service.CreateAsync(new("race.user", app.Password), actor, default); return 201; } catch (ApiException error) { return error.Status; } }));
        Assert.Equal(new[] { 201, 409 }, outcomes.Order().ToArray());
        Assert.Equal(1, await app.Service<MongoStore>().Users.CountDocumentsAsync(x => x.Username == "race.user"));
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "user.create" && x.Status == "success"));
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "user.create" && x.Status == "failure"));
    }
    // Requery missing and malformed reads and prove neither changes account state.
    [Theory] [InlineData("malformed", 400)] [InlineData("000000000000000000000000", 404)]
    public async Task Invalid_or_missing_read_returns_controlled_failure(string id, int status)
    {
        await using var app = new TestApplication();
        var error = await Assert.ThrowsAsync<ApiException>(() => app.Service<UserService>().ReadAsync(id, ObjectId.GenerateNewId(), default));
        Assert.Equal(status, error.Status); Assert.Equal(2, await app.Service<MongoStore>().Users.CountDocumentsAsync(FilterDefinition<UserRecord>.Empty));
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "user.read" && x.Status == "failure"));
    }
    // Force audit insertion failure inside the real transaction and prove user rollback.
    [Fact]
    public async Task Audit_failure_rolls_back_creation_and_cannot_return_successful_read()
    {
        await using var app = new TestApplication(); var store = app.Service<MongoStore>();
        await store.Database.CreateCollectionAsync("logs");
        await store.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "logs" }, { "validator", new BsonDocument("action", "login") }, { "validationLevel", "strict" } });
        await Assert.ThrowsAnyAsync<MongoException>(() => app.Service<UserService>().CreateAsync(new("rollback.user", app.Password), ObjectId.GenerateNewId(), default));
        Assert.Null(await app.Service<UserRepository>().FindByUsernameAsync("rollback.user", default));
        var user = (await app.Service<UserRepository>().FindByUsernameAsync("test.user", default))!;
        await Assert.ThrowsAnyAsync<MongoException>(() => app.Service<UserService>().ReadAsync(user.Id.ToString(), ObjectId.GenerateNewId(), default));
        Assert.Equal(0, await store.Logs.CountDocumentsAsync(FilterDefinition<AuditEvent>.Empty));
    }
    // Verify dependency failure for both use cases without replacing Mongo with a fake.
    [Fact]
    public async Task Offline_service_cannot_write_or_return_a_success()
    {
        await using var app = new TestApplication(); var store = app.Service<MongoStore>();
        var offline = new MongoStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(app.Settings) { ["Mongo:Uri"] = "mongodb://127.0.0.1:1/" }).Build());
        var service = new UserService(offline, new UserRepository(offline), new AuditRepository(offline), new UserValidator(), app.Service<PasswordHasher>(), app.Service<IdentifierProtector>(), TimeProvider.System);
        await Assert.ThrowsAsync<TimeoutException>(() => service.CreateAsync(new("offline.user", app.Password), ObjectId.GenerateNewId(), default));
        await Assert.ThrowsAsync<TimeoutException>(() => service.ReadAsync(ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId(), default));
        Assert.Null(await app.Service<UserRepository>().FindByUsernameAsync("offline.user", default)); Assert.Equal(0, await store.Logs.CountDocumentsAsync(FilterDefinition<AuditEvent>.Empty));
    }
}
