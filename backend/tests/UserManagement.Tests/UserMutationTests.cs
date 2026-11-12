// Verify mutation state and atomic audit behavior using isolated real Mongo databases.
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
public sealed class UserMutationTests
{
    // Deserialize actual patch semantics, including absent and explicit null fields.
    private static UpdateUserRequest Patch(string json) => JsonSerializer.Deserialize<UpdateUserRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    // Requery complete documents and ensure only the approved fields change.
    [Fact]
    public async Task Update_preserves_secrets_lifecycle_and_untouched_fields_then_deactivate_retains_record()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10";
        var service = app.Service<UserService>(); var repo = app.Service<UserRepository>(); var actor = ObjectId.GenerateNewId();
        var created = await service.CreateAsync(new("mutation.user", app.Password, "Synthetic address", "Old city", "Country", "Education", "SYNTHETIC-A", "SYNTHETIC-P"), actor, default);
        var before = (await repo.FindByIdAsync(ObjectId.Parse(created.Id), default))!.ToBsonDocument();
        var result = await service.UpdateAsync(created.Id, Patch("""{"city":"New city","aadhaar_no":"SYNTHETIC-NEW"}"""), actor, default);
        var updated = (await repo.FindByIdAsync(ObjectId.Parse(created.Id), default))!;
        Assert.Equal("New city", result.City); Assert.Equal(created.CreatedAt, result.CreatedAt); Assert.True(result.UpdatedAt > created.UpdatedAt);
        Assert.Equal(DateTimeKind.Utc, updated.UpdatedAt.Kind); Assert.Equal(0, updated.UpdatedAt.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal("SYNTHETIC-NEW", app.Service<IdentifierProtector>().Unprotect("aadhaar_no", updated.AadhaarNo!));
        Assert.Equal(app.Service<IdentifierProtector>().BlindIndex("aadhaar_no", "SYNTHETIC-NEW"), updated.AadhaarIndex);
        var after = updated.ToBsonDocument(); foreach (var field in new[] { "city", "aadhaar_no", "aadhaar_index", "updated_at" }) { before.Remove(field); after.Remove(field); } Assert.Equal(before, after);
        var snapshot = updated.ToBsonDocument(); var inactive = await service.DeactivateAsync(created.Id, actor, default);
        var retained = (await repo.FindByIdAsync(updated.Id, default))!.ToBsonDocument(); Assert.False(inactive.IsActive); Assert.True(inactive.UpdatedAt > result.UpdatedAt);
        snapshot.Remove("is_active"); snapshot.Remove("updated_at"); retained.Remove("is_active"); retained.Remove("updated_at"); Assert.Equal(snapshot, retained);
        await service.UpdateAsync(created.Id, Patch("""{"address":null,"pan_no":null}"""), actor, default);
        var cleared = (await repo.FindByIdAsync(updated.Id, default))!; Assert.Null(cleared.Address); Assert.Null(cleared.PanNo); Assert.Null(cleared.PanIndex); Assert.False(cleared.IsActive);
        var logs = await app.Service<MongoStore>().Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument("action", new BsonDocument("$in", new BsonArray { "user.update", "user.deactivate" }))).ToListAsync();
        Assert.Equal(3, logs.Count); foreach (var log in logs) { Assert.Equal(new[] { "_id", "action", "status", "timestamp", "user_id" }, log.Names.Order().ToArray()); Assert.Equal("success", log["status"]); Assert.DoesNotContain("SYNTHETIC", log.ToJson()); }
    }
    // Exercise every bound and operator-injection boundary before any write.
    [Theory]
    [InlineData("username", 2)] [InlineData("username", 65)] [InlineData("address", 501)] [InlineData("city", 101)]
    [InlineData("country", 101)] [InlineData("education", 201)] [InlineData("aadhaar_no", 65)] [InlineData("pan_no", 65)]
    public async Task Invalid_effective_patch_preserves_entire_document(string field, int size)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; var repo = app.Service<UserRepository>();
        var current = (await repo.FindByUsernameAsync("test.user", default))!; var before = current.ToBsonDocument();
        var patch = Patch(JsonSerializer.Serialize(new Dictionary<string, string> { [field] = new string('a', size), ["city"] = field == "city" ? new string('a', size) : "Should not persist" }));
        var error = await Assert.ThrowsAsync<ApiException>(() => app.Service<UserService>().UpdateAsync(current.Id.ToString(), patch, ObjectId.GenerateNewId(), default)); Assert.Equal(400, error.Status);
        Assert.Equal(before, (await repo.FindByIdAsync(current.Id, default))!.ToBsonDocument());
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "user.update" && x.Status == "failure"));
    }
    // Reject null required fields, hostile types and control characters without partial writes.
    [Theory] [InlineData("""{"username":null}""")] [InlineData("""{"city":{"$set":"bad"}}""")]
    [InlineData("""{"city":"bad\ncity"}""")] [InlineData("""{"username":"../admin"}""")]
    public async Task Hostile_patch_has_no_partial_write(string json)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; var repo = app.Service<UserRepository>(); var current = (await repo.FindByUsernameAsync("test.user", default))!;
        var error = await Assert.ThrowsAsync<ApiException>(() => app.Service<UserService>().UpdateAsync(current.Id.ToString(), Patch(json), ObjectId.GenerateNewId(), default)); Assert.Equal(400, error.Status);
        Assert.Equal(current.ToBsonDocument(), (await repo.FindByIdAsync(current.Id, default))!.ToBsonDocument());
    }
    // Validate the effective stored record, not merely supplied members.
    [Fact]
    public async Task Invalid_untouched_record_blocks_update()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; var store = app.Service<MongoStore>(); var repo = app.Service<UserRepository>(); var current = (await repo.FindByUsernameAsync("test.user", default))!;
        await store.Users.UpdateOneAsync(x => x.Id == current.Id, Builders<UserRecord>.Update.Set(x => x.Address, new string('a', 501)));
        var before = (await repo.FindByIdAsync(current.Id, default))!.ToBsonDocument();
        var error = await Assert.ThrowsAsync<ApiException>(() => app.Service<UserService>().UpdateAsync(current.Id.ToString(), Patch("""{"city":"New city"}"""), ObjectId.GenerateNewId(), default)); Assert.Equal(400, error.Status);
        Assert.Equal(before, (await repo.FindByIdAsync(current.Id, default))!.ToBsonDocument());
    }
    // Force real success-audit validation failure and prove rollback for both mutations.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Success_audit_failure_rolls_back_user_write_and_records_one_safe_failure(bool deactivate)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; var store = app.Service<MongoStore>(); var repo = app.Service<UserRepository>(); var current = (await repo.FindByUsernameAsync("test.user", default))!;
        await store.Database.CreateCollectionAsync("logs");
        await store.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "logs" }, { "validator", new BsonDocument("status", "failure") }, { "validationLevel", "strict" } });
        var service = app.Service<UserService>();
        await Assert.ThrowsAnyAsync<MongoException>(() => deactivate ? service.DeactivateAsync(current.Id.ToString(), ObjectId.GenerateNewId(), default) : service.UpdateAsync(current.Id.ToString(), Patch("""{"city":"Rollback"}"""), ObjectId.GenerateNewId(), default));
        Assert.Equal(current.ToBsonDocument(), (await repo.FindByIdAsync(current.Id, default))!.ToBsonDocument());
        Assert.Equal(1, await store.Logs.CountDocumentsAsync(x => x.Status == "failure")); Assert.Equal(0, await store.Logs.CountDocumentsAsync(x => x.Status == "success"));
    }
    // Distinguish malformed and absent routes for both service operations.
    [Theory] [InlineData(false, "invalid", 400)] [InlineData(true, "invalid", 400)]
    [InlineData(false, "000000000000000000000000", 404)] [InlineData(true, "000000000000000000000000", 404)]
    public async Task Missing_or_invalid_has_no_write(bool deactivate, string id, int status)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; var service = app.Service<UserService>();
        var error = await Assert.ThrowsAsync<ApiException>(() => deactivate ? service.DeactivateAsync(id, ObjectId.GenerateNewId(), default) : service.UpdateAsync(id, Patch("{}"), ObjectId.GenerateNewId(), default)); Assert.Equal(status, error.Status);
        Assert.Equal(2, await app.Service<MongoStore>().Users.CountDocumentsAsync(FilterDefinition<UserRecord>.Empty));
    }
}
