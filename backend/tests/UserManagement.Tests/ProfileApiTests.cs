// Exercise principal isolation, redacted failure auditing and real credential invalidation over HTTP.
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UserManagement.Audit;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Application;
using UserManagement.Domain;
using UserManagement.Infrastructure;
using UserManagement.Security;
using Xunit;
namespace UserManagement.Tests;
public sealed class ProfileApiTests
{
    // Obtain a real signed bearer through the audited login endpoint.
    private static async Task SignIn(TestApplication app, HttpClient client, string username = "test.user", string? password = null)
    {
        var response = await client.PostAsJsonAsync("/api/login", new LoginRequest(username, password ?? app.Password)); Assert.Equal(200, (int)response.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
    }
    // Check the safe no-store envelope and prohibit credential/driver disclosure.
    private static async Task Error(HttpResponseMessage response, int status, string? secret = null)
    {
        Assert.Equal(status, (int)response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(status, body.GetProperty("status").GetInt32());
        Assert.Equal(new[] { "correlationId", "errors", "status", "title", "type" }, body.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.DoesNotContain("Mongo", body.ToString()); Assert.DoesNotContain("SYNTHETIC", body.ToString()); if (secret is not null) Assert.DoesNotContain(secret, body.ToString());
    }
    // Both roles operate on themselves, with safe projections and no changes to the other account.
    [Theory] [InlineData("test.user")] [InlineData("test.admin")]
    public async Task Read_and_four_field_update_are_principal_only(string username)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client, username);
        var repo = app.Service<UserRepository>(); var current = (await repo.FindByUsernameAsync(username, default))!;
        var other = (await repo.FindByUsernameAsync(username == "test.user" ? "test.admin" : "test.user", default))!;
        var read = await client.GetAsync("/api/profile"); Assert.Equal(200, (int)read.StatusCode); var safe = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(current.Id.ToString(), safe.GetProperty("id").GetString()); Assert.Equal(new[] { "address", "city", "country", "created_at", "education", "id", "is_active", "updated_at", "username" }, safe.EnumerateObject().Select(x => x.Name).Order().ToArray());
        var response = await client.PutAsJsonAsync("/api/profile", new { address = "Synthetic address", city = "Synthetic city", country = "Country", education = "Education" }); Assert.Equal(200, (int)response.StatusCode);
        var stored = (await repo.FindByIdAsync(current.Id, default))!; Assert.Equal("Synthetic city", stored.City); Assert.Equal("Synthetic address", stored.Address); Assert.Equal("Country", stored.Country); Assert.Equal("Education", stored.Education); Assert.Equal(current.PasswordHash, stored.PasswordHash); Assert.Equal(current.CredentialVersion, stored.CredentialVersion);
        Assert.Equal(other.ToBsonDocument(), (await repo.FindByIdAsync(other.Id, default))!.ToBsonDocument()); Assert.True(stored.UpdatedAt > current.UpdatedAt);
        var fresh = await client.GetFromJsonAsync<JsonElement>("/api/profile"); Assert.Equal("Synthetic city", fresh.GetProperty("city").GetString());
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "profile.update" && x.Status == "success"));
    }
    // Reject cross-account, privilege, credential, lifecycle and sensitive-identifier mass assignment.
    [Theory] [InlineData("id")] [InlineData("userId")] [InlineData("role")] [InlineData("username")]
    [InlineData("password")] [InlineData("passwordHash")] [InlineData("credential_version")]
    [InlineData("aadhaar_no")] [InlineData("pan_no")] [InlineData("aadhaar_index")] [InlineData("pan_index")]
    [InlineData("is_active")] [InlineData("created_at")] [InlineData("updated_at")]
    public async Task Unknown_profile_members_do_not_mutate_either_account(string field)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        var repo = app.Service<UserRepository>(); var current = (await repo.FindByUsernameAsync("test.user", default))!; var other = (await repo.FindByUsernameAsync("test.admin", default))!;
        await Error(await client.PutAsJsonAsync("/api/profile", new Dictionary<string, object> { [field] = other.Id.ToString(), ["city"] = "SYNTHETIC" }), 400);
        Assert.Equal(current.ToBsonDocument(), (await repo.FindByIdAsync(current.Id, default))!.ToBsonDocument()); Assert.Equal(other.ToBsonDocument(), (await repo.FindByIdAsync(other.Id, default))!.ToBsonDocument());
        var log = await app.Service<MongoStore>().Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument("action", "profile.update")).SingleAsync(); Assert.Equal("failure", log["status"]); Assert.Equal(current.Id, log["user_id"].AsObjectId); Assert.Equal(5, log.ElementCount); Assert.DoesNotContain("SYNTHETIC", log.ToJson());
    }
    // Model-binding and service validation failures produce one durable redacted audit each.
    [Theory] [InlineData("not-json")] [InlineData("null")] [InlineData("[]")]
    [InlineData("""{"city":{"$set":"bad"}}""")] [InlineData("""{"education":true}""")] [InlineData("""{"address":"bad\ntext"}""")]
    public async Task Invalid_profile_body_is_400_and_inert(string body)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        var repo = app.Service<UserRepository>(); var before = (await repo.FindByUsernameAsync("test.user", default))!;
        await Error(await client.PutAsync("/api/profile", new StringContent(body, Encoding.UTF8, "application/json")), 400);
        Assert.Equal(before.ToBsonDocument(), (await repo.FindByIdAsync(before.Id, default))!.ToBsonDocument()); Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "profile.update" && x.Status == "failure"));
    }
    // Missing or hostile password bodies never modify stored credentials and are audited once.
    [Theory] [InlineData("{}")] [InlineData("null")] [InlineData("[]")] [InlineData("not-json")]
    [InlineData("""{"currentPassword":12,"newPassword":"SYNTHETIC-password"}""")]
    [InlineData("""{"currentPassword":"wrong-password","newPassword":"SYNTHETIC-password","id":"other"}""")]
    [InlineData("""{"currentPassword":"wrong-password","newPassword":"SYNTHETIC-password","role":"Admin"}""")]
    public async Task Invalid_password_body_preserves_credential(string body)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        var repo = app.Service<UserRepository>(); var before = (await repo.FindByUsernameAsync("test.user", default))!;
        await Error(await client.PostAsync("/api/profile/password", new StringContent(body, Encoding.UTF8, "application/json")), 400, app.Password);
        Assert.Equal(before.ToBsonDocument(), (await repo.FindByIdAsync(before.Id, default))!.ToBsonDocument()); Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "password.change" && x.Status == "failure"));
    }
    // Wrong proof and invalid UTF-8 bounds preserve old credentials and the still-current bearer.
    [Theory] [InlineData(false, 20, false)] [InlineData(true, 11, false)] [InlineData(true, 73, false)] [InlineData(true, 37, true)]
    public async Task Invalid_password_is_inert_and_old_token_still_works(bool validCurrent, int size, bool unicode)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        var repo = app.Service<UserRepository>(); var before = (await repo.FindByUsernameAsync("test.user", default))!;
        await Error(await client.PostAsJsonAsync("/api/profile/password", new PasswordChangeRequest(validCurrent ? app.Password : "wrong-current-password", new string(unicode ? 'é' : 'a', size))), 400, app.Password);
        Assert.Equal(before.ToBsonDocument(), (await repo.FindByIdAsync(before.Id, default))!.ToBsonDocument()); Assert.Equal(200, (int)(await client.GetAsync("/api/profile")).StatusCode);
    }
    // Real persisted version invalidates every old bearer and old password; another account is unaffected.
    [Theory] [InlineData("test.user")] [InlineData("test.admin")]
    public async Task Password_change_returns_204_invalidates_old_tokens_and_accepts_new_login(string username)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); using var second = app.CreateClient(); using var other = app.CreateClient();
        await SignIn(app, client, username); await SignIn(app, second, username); await SignIn(app, other, username == "test.user" ? "test.admin" : "test.user");
        var repo = app.Service<UserRepository>(); var before = (await repo.FindByUsernameAsync(username, default))!; const string next = "SYNTHETIC-new-password";
        var changed = await client.PostAsJsonAsync("/api/profile/password", new PasswordChangeRequest(app.Password, next)); Assert.Equal(204, (int)changed.StatusCode); Assert.Equal("", await changed.Content.ReadAsStringAsync());
        var stored = (await repo.FindByIdAsync(before.Id, default))!; Assert.Equal(before.CredentialVersion + 1, stored.CredentialVersion); Assert.StartsWith("$2a$12$", stored.PasswordHash); Assert.True(app.Service<PasswordHasher>().Verify(next, stored.PasswordHash));
        await Error(await client.GetAsync("/api/profile"), 401); await Error(await second.PutAsJsonAsync("/api/profile", new { city = "SYNTHETIC" }), 401); await Error(await client.PostAsJsonAsync("/api/profile/password", new PasswordChangeRequest(next, app.Password)), 401);
        Assert.Equal(200, (int)(await other.GetAsync("/api/profile")).StatusCode); await Error(await client.PostAsJsonAsync("/api/login", new LoginRequest(username, app.Password)), 401, app.Password);
        await SignIn(app, client, username, next); Assert.Equal(before.Id.ToString(), (await client.GetFromJsonAsync<JsonElement>("/api/profile")).GetProperty("id").GetString());
        Assert.Equal(stored.ToBsonDocument(), (await repo.FindByIdAsync(before.Id, default))!.ToBsonDocument());
        var logs = await app.Service<MongoStore>().Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument("action", "password.change")).ToListAsync(); Assert.Equal(2, logs.Count); Assert.Single(logs, x => x["status"] == "success"); foreach (var log in logs) { Assert.DoesNotContain(next, log.ToJson()); Assert.DoesNotContain(app.Password, log.ToJson()); }
    }
    // Anonymous, deactivated and removed accounts fail the real DB-backed JWT gate for all methods.
    [Theory] [InlineData("anonymous")] [InlineData("inactive")] [InlineData("removed")]
    public async Task Invalid_principal_cannot_read_or_write(string state)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); if (state != "anonymous") await SignIn(app, client);
        var store = app.Service<MongoStore>(); var current = (await app.Service<UserRepository>().FindByUsernameAsync("test.user", default))!;
        if (state == "inactive") await store.Users.UpdateOneAsync(x => x.Id == current.Id, Builders<UserRecord>.Update.Set(x => x.IsActive, false)); if (state == "removed") await store.Users.DeleteOneAsync(x => x.Id == current.Id);
        await Error(await client.GetAsync("/api/profile"), 401); await Error(await client.PutAsJsonAsync("/api/profile", new { city = "SYNTHETIC" }), 401); await Error(await client.PostAsJsonAsync("/api/profile/password", new PasswordChangeRequest(app.Password, "new-valid-password")), 401);
        var logs = await store.Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument("action", new BsonDocument("$in", new BsonArray { "profile.update", "password.change" }))).ToListAsync(); Assert.Equal(2, logs.Count); Assert.All(logs, x => { Assert.Equal("failure", x["status"]); Assert.False(x.Contains("user_id")); });
        if (state != "removed") { var retained = (await app.Service<UserRepository>().FindByIdAsync(current.Id, default))!; Assert.Equal(current.PasswordHash, retained.PasswordHash); Assert.Equal(current.City, retained.City); }
    }
    // No alternate target route exists; cross-account directory access remains forbidden to User.
    [Fact]
    public async Task User_cannot_address_another_profile_or_directory_record()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        var other = (await app.Service<UserRepository>().FindByUsernameAsync("test.admin", default))!;
        Assert.Equal(404, (int)(await client.PutAsJsonAsync("/api/profile/" + other.Id, new { city = "SYNTHETIC" })).StatusCode);
        await Error(await client.PutAsJsonAsync("/api/users/" + other.Id, new { city = "SYNTHETIC" }), 403);
        Assert.Equal(other.ToBsonDocument(), (await app.Service<UserRepository>().FindByIdAsync(other.Id, default))!.ToBsonDocument());
    }
    // Exercise every HTTP optional-field bound and prove no partial update.
    [Theory] [InlineData("address", 500)] [InlineData("city", 100)] [InlineData("country", 100)] [InlineData("education", 200)]
    public async Task Optional_field_maximum_is_inclusive_and_oversize_is_inert(string field, int maximum)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        var repo = app.Service<UserRepository>(); var before = (await repo.FindByUsernameAsync("test.user", default))!;
        await Error(await client.PutAsJsonAsync("/api/profile", new Dictionary<string, string> { [field] = new string('a', maximum + 1) }), 400);
        Assert.Equal(before.ToBsonDocument(), (await repo.FindByIdAsync(before.Id, default))!.ToBsonDocument());
        Assert.Equal(200, (int)(await client.PutAsJsonAsync("/api/profile", new Dictionary<string, string> { [field] = new string('a', maximum) })).StatusCode);
        Assert.Equal(new string('a', maximum), (await repo.FindByIdAsync(before.Id, default))!.ToBsonDocument()[field].AsString);
    }
    // Disconnect fresh real repositories and require safe dependency errors, not an in-memory fallback.
    [Fact]
    public async Task Offline_dependency_returns_safe_503_without_user_write()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10";
        using var host = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => { services.RemoveAll<UserRepository>(); services.AddTransient<UserRepository>(); services.RemoveAll<AuditRepository>(); services.AddTransient<AuditRepository>(); }));
        using var client = host.CreateClient(); await SignIn(app, client); var before = (await host.Services.GetRequiredService<UserRepository>().FindByUsernameAsync("test.user", default))!;
        app.DependencyUnavailable = true; app.Settings["Mongo:TimeoutSeconds"] = "1";
        try
        {
            await Error(await client.GetAsync("/api/profile"), 503);
            await Error(await client.PutAsJsonAsync("/api/profile", new { city = "SYNTHETIC" }), 503);
            await Error(await client.PostAsJsonAsync("/api/profile/password", new PasswordChangeRequest(app.Password, "new-valid-password")), 503);
            client.DefaultRequestHeaders.Authorization = null; await Error(await client.PutAsJsonAsync("/api/profile", new { city = "SYNTHETIC" }), 503);
        }
        finally { app.DependencyUnavailable = false; app.Settings["Mongo:TimeoutSeconds"] = "10"; }
        Assert.Equal(before.ToBsonDocument(), (await host.Services.GetRequiredService<UserRepository>().FindByIdAsync(before.Id, default))!.ToBsonDocument());
    }
    // Reject real audit writes after login and prove HTTP 503 does not leave a partial mutation.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Audit_failure_is_safe_503_and_rolls_back(bool password)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        var store = app.Service<MongoStore>(); var before = (await app.Service<UserRepository>().FindByUsernameAsync("test.user", default))!;
        await store.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "logs" }, { "validator", new BsonDocument("action", "login") }, { "validationLevel", "strict" } });
        await Error(password ? await client.PostAsJsonAsync("/api/profile/password", new PasswordChangeRequest(app.Password, "new-valid-password")) : await client.PutAsJsonAsync("/api/profile", new { city = "SYNTHETIC" }), 503, app.Password);
        Assert.Equal(before.ToBsonDocument(), (await app.Service<UserRepository>().FindByIdAsync(before.Id, default))!.ToBsonDocument()); Assert.Equal(1, await store.Logs.CountDocumentsAsync(FilterDefinition<AuditEvent>.Empty));
    }
}
