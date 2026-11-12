// Exercise only account create/read HTTP contracts against real isolated Mongo state.
using System.Net;
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
// Prove authorization, mass-assignment rejection, safe projections and persistence at the boundary.
public sealed class UserApiTests
{
    // Authenticate through the actual login endpoint, retaining its durable audit behavior.
    private static async Task SignIn(TestApplication app, HttpClient client, string username = "test.admin")
    {
        var response = await client.PostAsJsonAsync("/api/login", new LoginRequest(username, app.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
    }
    // Assert the exact safe projection so new internal fields cannot silently leak.
    private static void SafeUser(JsonElement json)
    {
        Assert.Equal(new[] { "address", "city", "country", "created_at", "education", "id", "is_active", "updated_at", "username" }, json.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.True(ObjectId.TryParse(json.GetProperty("id").GetString(), out _)); Assert.True(json.GetProperty("is_active").GetBoolean());
    }
    // Verify redaction and the canonical error shape rather than only the response code.
    private static async Task Error(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "correlationId", "errors", "status", "title", "type" }, body.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal((int)status, body.GetProperty("status").GetInt32()); Assert.False(string.IsNullOrEmpty(body.GetProperty("correlationId").GetString()));
        Assert.DoesNotContain("synthetic-password", body.ToString()); Assert.DoesNotContain("mongodb", body.ToString());
    }
    // Persist encrypted fields through real POST then prove GET has no account mutation.
    [Fact]
    public async Task Admin_create_and_read_return_safe_fields_location_and_durable_outcomes()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        var request = new CreateUserRequest("http.user", app.Password, "Synthetic address", "Synthetic city", "Synthetic country", "Synthetic education", "SYNTHETIC-AADHAAR", "SYNTHETIC-PAN");
        var response = await client.PostAsJsonAsync("/api/users", request); Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(); SafeUser(json); Assert.Equal(request.Username, json.GetProperty("username").GetString());
        var id = json.GetProperty("id").GetString()!; Assert.EndsWith("/api/users/" + id, response.Headers.Location!.ToString());
        var stored = (await app.Service<UserRepository>().FindByIdAsync(ObjectId.Parse(id), default))!; var before = stored.ToBsonDocument();
        Assert.True(app.Service<PasswordHasher>().Verify(app.Password, stored.PasswordHash)); Assert.Equal("12", BCrypt.Net.BCrypt.InterrogateHash(stored.PasswordHash).WorkFactor);
        Assert.Equal(UserRole.User, stored.Role); Assert.Equal(1, stored.CredentialVersion); Assert.Equal(stored.CreatedAt, stored.UpdatedAt);
        Assert.Equal(request.AadhaarNo, app.Service<IdentifierProtector>().Unprotect("aadhaar_no", stored.AadhaarNo!)); Assert.Equal(request.PanNo, app.Service<IdentifierProtector>().Unprotect("pan_no", stored.PanNo!));
        var read = await client.GetAsync("/api/users/" + id); Assert.Equal(HttpStatusCode.OK, read.StatusCode); SafeUser(await read.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal(before, (await app.Service<UserRepository>().FindByIdAsync(stored.Id, default))!.ToBsonDocument());
        var logs = await app.Service<MongoStore>().Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument("action", new BsonDocument("$in", new BsonArray { "user.create", "user.read" }))).ToListAsync();
        Assert.Equal(2, logs.Count);
        var actor = (await app.Service<UserRepository>().FindByUsernameAsync("test.admin", default))!.Id;
        foreach (var log in logs) { Assert.Equal(new[] { "_id", "action", "status", "timestamp", "user_id" }, log.Names.Order().ToArray()); Assert.Equal(actor, log["user_id"].AsObjectId); Assert.Equal("success", log["status"].AsString); Assert.DoesNotContain(request.Username!, log.ToJson()); Assert.DoesNotContain(app.Password, log.ToJson()); }
    }
    // Reject malformed binding, each missing credential, injection and server-owned fields before writes.
    [Theory]
    [InlineData("""{}""")] [InlineData("""{"username":"http.user"}""")] [InlineData("""{"password":"synthetic-password"}""")]
    [InlineData("""{"username":12,"password":"synthetic-password"}""")]
    [InlineData("""{"username":{"$ne":null},"password":"synthetic-password"}""")]
    [InlineData("""{"username":"../admin","password":"synthetic-password"}""")]
    [InlineData("""{"username":"<script>","password":"synthetic-password"}""")]
    [InlineData("""{"username":"http.user","password":"short"}""")]
    [InlineData("""{"username":"http.user","password":"synthetic-password","role":"Admin"}""")]
    [InlineData("""{"username":"http.user","password":"synthetic-password","is_active":false}""")]
    [InlineData("""{"username":"http.user","password":"synthetic-password","credential_version":3}""")]
    [InlineData("""{"username":"http.user","password":"synthetic-password","created_at":"2026-01-01"}""")]
    [InlineData("""{"username":"http.user","password":"synthetic-password","pan_index":"fake"}""")]
    [InlineData("""{"username":"http.user","password":"synthetic-password","city":[]}""")]
    [InlineData("not-json")]
    public async Task Invalid_create_returns_400_no_user_write_and_one_redacted_failure(string body)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        await Error(await client.PostAsync("/api/users", new StringContent(body, Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest);
        Assert.Equal(2, await app.Service<MongoStore>().Users.CountDocumentsAsync(FilterDefinition<UserRecord>.Empty));
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "user.create" && x.Status == "failure"));
    }
    // Assert optional and UTF-8 bounds at the HTTP boundary as well as the service layer.
    [Theory] [InlineData("address", 501)] [InlineData("city", 101)] [InlineData("country", 101)] [InlineData("education", 201)]
    [InlineData("aadhaar_no", 65)] [InlineData("pan_no", 65)] [InlineData("password", 73)] [InlineData("utf8", 37)] [InlineData("username", 65)]
    public async Task Oversized_fields_are_rejected_without_persistence(string field, int length)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        var request = new Dictionary<string, object?> { ["username"] = "http.user", ["password"] = app.Password };
        request[field == "utf8" ? "password" : field] = new string(field == "utf8" ? 'é' : 'a', length);
        await Error(await client.PostAsJsonAsync("/api/users", request), HttpStatusCode.BadRequest);
        Assert.Equal(2, await app.Service<MongoStore>().Users.CountDocumentsAsync(FilterDefinition<UserRecord>.Empty));
    }
    // Enforce both account endpoints for unknown and non-administrator callers and audit denied actor safely.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Unknown_and_User_callers_receive_401_or_403_without_account_write(bool signIn)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); if (signIn) await SignIn(app, client, "test.user");
        var status = signIn ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
        await Error(await client.PostAsJsonAsync("/api/users", new CreateUserRequest("denied.user", app.Password)), status);
        await Error(await client.GetAsync("/api/users/000000000000000000000000"), status);
        Assert.Equal(2, await app.Service<MongoStore>().Users.CountDocumentsAsync(FilterDefinition<UserRecord>.Empty));
        var logs = await app.Service<MongoStore>().Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument("status", "failure")).ToListAsync(); Assert.Equal(2, logs.Count);
        foreach (var log in logs) { Assert.Equal(signIn, log.Contains("user_id")); Assert.Equal(signIn ? 5 : 4, log.ElementCount); Assert.DoesNotContain("denied.user", log.ToJson()); }
    }
    // Distinguish invalid identity from a missing document and audit both read failures.
    [Theory] [InlineData("not-an-id", 400)] [InlineData("000000000000000000000000", 404)]
    public async Task Bad_and_absent_read_return_controlled_status_and_no_mutation(string id, int status)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        await Error(await client.GetAsync("/api/users/" + id), (HttpStatusCode)status);
        Assert.Equal(2, await app.Service<MongoStore>().Users.CountDocumentsAsync(FilterDefinition<UserRecord>.Empty));
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "user.read" && x.Status == "failure"));
    }
    // Race real HTTP writes then requery the single winner and both audit outcomes.
    [Fact]
    public async Task Concurrent_duplicate_HTTP_requests_have_one_201_and_one_409()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await SignIn(app, client);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.PostAsJsonAsync("/api/users", new CreateUserRequest("race.http", app.Password))));
        Assert.Equal(new[] { 201, 409 }, responses.Select(x => (int)x.StatusCode).Order().ToArray());
        await Error(responses.Single(x => x.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict);
        Assert.Equal(1, await app.Service<MongoStore>().Users.CountDocumentsAsync(x => x.Username == "race.http"));
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "user.create" && x.Status == "success"));
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "user.create" && x.Status == "failure"));
    }
    // Require account and audit durability even when the dependency cannot be reached.
    [Fact]
    public async Task Dependency_failure_returns_safe_503_for_create_read_and_unauthenticated_denial()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10";
        // Resolve fresh real repositories for this fault-injection host so the dynamic store switch
        // reaches account services instead of the baseline singleton repositories captured at seed time.
        using var host = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<UserRepository>(); services.AddTransient<UserRepository>();
            services.RemoveAll<AuditRepository>(); services.AddTransient<AuditRepository>();
        }));
        using var client = host.CreateClient(); await SignIn(app, client); app.DependencyUnavailable = true;
        app.Settings["Mongo:TimeoutSeconds"] = "1";
        try
        {
            await Error(await client.PostAsJsonAsync("/api/users", new CreateUserRequest("offline.http", app.Password)), HttpStatusCode.ServiceUnavailable);
            await Error(await client.GetAsync("/api/users/000000000000000000000000"), HttpStatusCode.ServiceUnavailable);
            client.DefaultRequestHeaders.Authorization = null;
            await Error(await client.PostAsJsonAsync("/api/users", new CreateUserRequest("offline.http", app.Password)), HttpStatusCode.ServiceUnavailable);
        }
        finally { app.DependencyUnavailable = false; app.Settings["Mongo:TimeoutSeconds"] = "10"; }
        var store = host.Services.GetRequiredService<MongoStore>();
        Assert.Null(await new UserRepository(store).FindByUsernameAsync("offline.http", default));
    }
}
