// Exercise Admin mutation HTTP boundaries against real persisted Mongo state.
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Application;
using UserManagement.Audit;
using UserManagement.Domain;
using UserManagement.Infrastructure;
using UserManagement.Security;
using Xunit;
namespace UserManagement.Tests;
public sealed class UserMutationApiTests
{
    // Obtain a genuine bearer through the durable login endpoint.
    private static async Task SignIn(TestApplication app, HttpClient client, string username = "test.admin")
    {
        var response = await client.PostAsJsonAsync("/api/login", new LoginRequest(username, app.Password)); Assert.Equal(200, (int)response.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
    }
    // Check safe envelope and prohibit payload/driver detail leakage.
    private static async Task Error(HttpResponseMessage response, int status)
    {
        Assert.Equal(status, (int)response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "correlationId", "errors", "status", "title", "type" }, json.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(status, json.GetProperty("status").GetInt32()); Assert.DoesNotContain("SYNTHETIC", json.ToString()); Assert.DoesNotContain("Mongo", json.ToString());
    }
    // Assert mutation response shape and requery retained inactive storage.
    [Fact]
    public async Task Admin_patch_and_delete_return_safe_user_and_retain_inactive_record()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        var repo = app.Service<UserRepository>(); var current = (await repo.FindByUsernameAsync("test.user", default))!;
        var response = await client.PutAsJsonAsync("/api/users/" + current.Id, new { username = "changed.user", city = "Synthetic city", pan_no = "SYNTHETIC-PAN" }); Assert.Equal(200, (int)response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "address", "city", "country", "created_at", "education", "id", "is_active", "updated_at", "username" }, body.EnumerateObject().Select(x => x.Name).Order().ToArray());
        var updated = (await repo.FindByIdAsync(current.Id, default))!; Assert.Equal("changed.user", updated.Username); Assert.Equal(current.PasswordHash, updated.PasswordHash); Assert.Equal(current.CreatedAt, updated.CreatedAt);
        Assert.Equal("SYNTHETIC-PAN", app.Service<IdentifierProtector>().Unprotect("pan_no", updated.PanNo!));
        var deleted = await client.DeleteAsync("/api/users/" + current.Id); Assert.Equal(200, (int)deleted.StatusCode); Assert.False((await deleted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("is_active").GetBoolean());
        var retained = (await repo.FindByIdAsync(current.Id, default))!; Assert.False(retained.IsActive); Assert.Equal(updated.PanNo, retained.PanNo); Assert.Equal(updated.PasswordHash, retained.PasswordHash);
        var read = await client.GetAsync("/api/users/" + current.Id); Assert.Equal(200, (int)read.StatusCode); Assert.False((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("is_active").GetBoolean());
    }
    // Reject mass assignment and hostile JSON with exactly one redacted failure event.
    [Theory]
    [InlineData("role", "Admin")] [InlineData("is_active", true)] [InlineData("created_at", "SYNTHETIC")]
    [InlineData("updated_at", "SYNTHETIC")] [InlineData("password", "SYNTHETIC")]
    [InlineData("credential_version", 9)] [InlineData("passwordHash", "SYNTHETIC")]
    [InlineData("aadhaar_index", "SYNTHETIC")] [InlineData("pan_index", "SYNTHETIC")]
    [InlineData("unknown", "SYNTHETIC")] [InlineData("id", "SYNTHETIC")]
    public async Task Server_owned_members_are_400_and_do_not_write(string field, object value)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        var repo = app.Service<UserRepository>(); var current = (await repo.FindByUsernameAsync("test.user", default))!;
        await Error(await client.PutAsJsonAsync("/api/users/" + current.Id, new Dictionary<string, object> { [field] = value, ["city"] = "SYNTHETIC" }), 400);
        Assert.Equal(current.ToBsonDocument(), (await repo.FindByIdAsync(current.Id, default))!.ToBsonDocument());
        var logs = await app.Service<MongoStore>().Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument("action", "user.update")).ToListAsync(); Assert.Single(logs);
        Assert.Equal(new[] { "_id", "action", "status", "timestamp", "user_id" }, logs[0].Names.Order().ToArray()); Assert.Equal("failure", logs[0]["status"]); Assert.DoesNotContain("SYNTHETIC", logs[0].ToJson());
    }
    // Cover malformed body/type boundaries and require no partial update.
    [Theory] [InlineData("not-json")] [InlineData("null")] [InlineData("[]")]
    [InlineData("""{"username":{"$ne":null}}""")] [InlineData("""{"city":12}""")]
    [InlineData("""{"username":null}""")] [InlineData("""{"city":"bad\ncity"}""")]
    public async Task Malformed_patch_is_400_and_inert(string body)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        var repo = app.Service<UserRepository>(); var current = (await repo.FindByUsernameAsync("test.user", default))!;
        await Error(await client.PutAsync("/api/users/" + current.Id, new StringContent(body, Encoding.UTF8, "application/json")), 400);
        Assert.Equal(current.ToBsonDocument(), (await repo.FindByIdAsync(current.Id, default))!.ToBsonDocument());
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "user.update" && x.Status == "failure"));
    }
    // Audit denials for both methods while keeping unknown actors omitted.
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Unknown_or_User_cannot_mutate(bool signedIn, bool deactivate)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); if (signedIn) await SignIn(app, client, "test.user");
        var repo = app.Service<UserRepository>(); var current = (await repo.FindByUsernameAsync("test.user", default))!;
        await Error(deactivate ? await client.DeleteAsync("/api/users/" + current.Id) : await client.PutAsJsonAsync("/api/users/" + current.Id, new { city = "SYNTHETIC" }), signedIn ? 403 : 401);
        Assert.Equal(current.ToBsonDocument(), (await repo.FindByIdAsync(current.Id, default))!.ToBsonDocument());
        var action = deactivate ? "user.deactivate" : "user.update";
        var log = await app.Service<MongoStore>().Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument("action", action)).SingleAsync();
        Assert.Equal(signedIn, log.Contains("user_id")); Assert.Equal(signedIn ? 5 : 4, log.ElementCount);
    }
    // Distinguish invalid identity from absent records for PUT and DELETE.
    [Theory] [InlineData(false, "invalid", 400)] [InlineData(true, "invalid", 400)]
    [InlineData(false, "000000000000000000000000", 404)] [InlineData(true, "000000000000000000000000", 404)]
    public async Task Missing_or_malformed_route_is_controlled(bool deactivate, string id, int status)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        await Error(deactivate ? await client.DeleteAsync("/api/users/" + id) : await client.PutAsJsonAsync("/api/users/" + id, new { city = "SYNTHETIC" }), status);
        Assert.Equal(2, await app.Service<MongoStore>().Users.CountDocumentsAsync(FilterDefinition<UserRecord>.Empty));
    }
    // Race different accounts for the same unique username and prove the losing patch rolls back.
    [Fact]
    public async Task Concurrent_duplicate_patch_has_one_winner_one_409_and_no_partial_city()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        var service = app.Service<UserService>(); var repo = app.Service<UserRepository>(); var actor = ObjectId.GenerateNewId();
        var a = await service.CreateAsync(new("race.a", app.Password, City: "Original"), actor, default); var b = await service.CreateAsync(new("race.b", app.Password, City: "Original"), actor, default);
        var responses = await Task.WhenAll(new[] { a, b }.Select(x => client.PutAsJsonAsync("/api/users/" + x.Id, new { username = "race.winner", city = "SYNTHETIC" })));
        Assert.Equal(new[] { 200, 409 }, responses.Select(x => (int)x.StatusCode).Order().ToArray());
        var documents = await app.Service<MongoStore>().Users.Find(x => x.Id == ObjectId.Parse(a.Id) || x.Id == ObjectId.Parse(b.Id)).ToListAsync();
        Assert.Single(documents, x => x.Username == "race.winner" && x.City == "SYNTHETIC"); Assert.Single(documents, x => x.Username != "race.winner" && x.City == "Original");
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "user.update" && x.Status == "success")); Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "user.update" && x.Status == "failure"));
    }
    // Concurrent snapshot retries preserve both disjoint edits and never reactivate a deactivated record.
    [Fact]
    public async Task Concurrent_same_record_edits_and_deactivation_preserve_all_changes()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        var current = (await app.Service<UserRepository>().FindByUsernameAsync("test.user", default))!; var url = "/api/users/" + current.Id;
        var responses = await Task.WhenAll(client.PutAsJsonAsync(url, new { city = "New city" }), client.PutAsJsonAsync(url, new { country = "New country" }), client.DeleteAsync(url));
        Assert.All(responses, x => Assert.Equal(200, (int)x.StatusCode)); var stored = (await app.Service<UserRepository>().FindByIdAsync(current.Id, default))!;
        Assert.Equal("New city", stored.City); Assert.Equal("New country", stored.Country); Assert.False(stored.IsActive); Assert.Equal(current.PasswordHash, stored.PasswordHash);
    }
    // Fault the real audit collection after login and require HTTP 503 with complete rollback.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Audit_dependency_failure_is_503_and_rolls_back(bool deactivate)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        var store = app.Service<MongoStore>(); var repo = app.Service<UserRepository>(); var current = (await repo.FindByUsernameAsync("test.user", default))!;
        await store.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "logs" }, { "validator", new BsonDocument("action", "login") }, { "validationLevel", "strict" } });
        await Error(deactivate ? await client.DeleteAsync("/api/users/" + current.Id) : await client.PutAsJsonAsync("/api/users/" + current.Id, new { city = "SYNTHETIC" }), 503);
        Assert.Equal(current.ToBsonDocument(), (await repo.FindByIdAsync(current.Id, default))!.ToBsonDocument());
        Assert.Equal(1, await store.Logs.CountDocumentsAsync(FilterDefinition<AuditEvent>.Empty));
    }
    // Disconnect fresh real repositories without replacing persistence with mocks.
    [Fact]
    public async Task Offline_dependency_is_safe_503_for_both_mutations_and_denial()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10";
        using var host = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => { services.RemoveAll<UserRepository>(); services.AddTransient<UserRepository>(); services.RemoveAll<AuditRepository>(); services.AddTransient<AuditRepository>(); }));
        using var client = host.CreateClient(); await SignIn(app, client); app.DependencyUnavailable = true; app.Settings["Mongo:TimeoutSeconds"] = "1";
        try { await Error(await client.PutAsJsonAsync("/api/users/000000000000000000000000", new { city = "SYNTHETIC" }), 503); await Error(await client.DeleteAsync("/api/users/000000000000000000000000"), 503); client.DefaultRequestHeaders.Authorization = null; await Error(await client.DeleteAsync("/api/users/000000000000000000000000"), 503); }
        finally { app.DependencyUnavailable = false; app.Settings["Mongo:TimeoutSeconds"] = "10"; }
        Assert.Equal(2, await host.Services.GetRequiredService<MongoStore>().Users.CountDocumentsAsync(FilterDefinition<UserRecord>.Empty));
    }
}
