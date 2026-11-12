// Verify Admin activity HTTP contracts and fail-closed security against real isolated Mongo.
using System.Net.Http.Json;
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
using Xunit;
namespace UserManagement.Tests;
// Exercise both activity routes through the production middleware, authorization and controllers.
public sealed class AuditDashboardApiTests
{
    // Obtain a genuine audited session without substituting authentication handlers.
    private static async Task Login(TestApplication app, HttpClient client, string username = "test.admin")
    {
        var response = await client.PostAsJsonAsync("/api/login", new LoginRequest(username, app.Password)); Assert.Equal(200, (int)response.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
    }
    // Require a safe structured no-store failure and prohibit stale activity or dependency disclosure.
    private static async Task Error(HttpResponseMessage response, int status)
    {
        Assert.Equal(status, (int)response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(status, body.GetProperty("status").GetInt32());
        Assert.Equal(new[] { "correlationId", "errors", "status", "title", "type" }, body.EnumerateObject().Select(x => x.Name).Order());
        Assert.DoesNotContain("Mongo", body.ToString()); Assert.DoesNotContain("PRIVATE", body.ToString());
    }
    // Logs have only approved exact fields, unknown actors omitted, and never profile or credential payloads.
    [Fact]
    public async Task Logs_are_safe_exact_and_read_only()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await Login(app, client);
        var store = app.Service<MongoStore>(); var log = AuditEvent.Create(null, "login", false).ToBsonDocument(); log["password"] = "PRIVATE"; log["address"] = "PRIVATE";
        await store.Database.GetCollection<BsonDocument>("logs").InsertOneAsync(log);
        var before = await store.Logs.CountDocumentsAsync(FilterDefinition<AuditEvent>.Empty);
        var response = await client.GetAsync("/api/logs"); Assert.Equal(200, (int)response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(new[] { "items" }, body.EnumerateObject().Select(x => x.Name));
        var rows = body.GetProperty("items").EnumerateArray().ToList(); Assert.Equal(2, rows.Count);
        foreach (var row in rows) Assert.Equal(row.TryGetProperty("user_id", out _) ? new[] { "action", "id", "status", "timestamp", "user_id" } : new[] { "action", "id", "status", "timestamp" }, row.EnumerateObject().Select(x => x.Name).Order());
        var unknown = rows.Single(x => x.GetProperty("id").GetString() == log["_id"].ToString()); Assert.False(unknown.TryGetProperty("user_id", out _)); Assert.Equal("failure", unknown.GetProperty("status").GetString());
        Assert.DoesNotContain("PRIVATE", body.ToString()); Assert.Equal(before, await store.Logs.CountDocumentsAsync(FilterDefinition<AuditEvent>.Empty));
    }
    // Six summaries agree with persisted status/countries and approved audit outcomes over HTTP.
    [Fact]
    public async Task Dashboard_returns_six_reconciled_source_summaries()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await Login(app, client); var store = app.Service<MongoStore>();
        await store.Users.InsertOneAsync(new UserRecord { Username = "synthetic.country", Country = "Country", IsActive = false });
        var old = AuditEvent.Create(null, "login", false); old.Timestamp = DateTime.UtcNow.AddDays(-500); await store.Logs.InsertOneAsync(old);
        var response = await client.GetAsync("/api/dashboard"); Assert.Equal(200, (int)response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(new[] { "active", "auditSummary", "countryDistribution", "inactive", "recentLogins", "total" }, body.EnumerateObject().Select(x => x.Name).Order());
        var result = body.Deserialize<DashboardView>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!; Assert.Equal(3, result.Total); Assert.Equal(2, result.Active); Assert.Equal(1, result.Inactive);
        Assert.Equal(new[] { new CountryCount("Country", 1) }, result.CountryDistribution); Assert.Equal(new[] { new AuditActivity("login", 1, 1) }, result.AuditSummary);
        Assert.Equal(old.Id.ToString(), result.RecentLogins[1].Id); Assert.Equal(new BsonDateTime(old.Timestamp).ToUniversalTime(), result.RecentLogins[1].Timestamp);
    }
    // Authorization must deny before activity data is exposed, including malformed bearer credentials.
    [Theory] [InlineData("/api/logs", "none", 401)] [InlineData("/api/dashboard", "none", 401)]
    [InlineData("/api/logs", "malformed", 401)] [InlineData("/api/dashboard", "malformed", 401)]
    [InlineData("/api/logs", "user", 403)] [InlineData("/api/dashboard", "user", 403)]
    public async Task Non_admin_is_denied(string path, string session, int status)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient();
        if (session == "user") await Login(app, client, "test.user"); if (session == "malformed") client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid");
        var store = app.Service<MongoStore>(); var count = await store.Logs.CountDocumentsAsync(FilterDefinition<AuditEvent>.Empty);
        await Error(await client.GetAsync(path), status); Assert.Equal(count, await store.Logs.CountDocumentsAsync(FilterDefinition<AuditEvent>.Empty));
    }
    // Stale credentials and deactivated administrator accounts lose access to both activity reads.
    [Theory] [InlineData("/api/logs", true)] [InlineData("/api/dashboard", true)] [InlineData("/api/logs", false)] [InlineData("/api/dashboard", false)]
    public async Task Revoked_admin_session_is_rejected(string path, bool inactive)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await Login(app, client); var store = app.Service<MongoStore>();
        await store.Users.UpdateOneAsync(x => x.Username == "test.admin", inactive ? Builders<UserRecord>.Update.Set(x => x.IsActive, false) : Builders<UserRecord>.Update.Inc(x => x.CredentialVersion, 1));
        await Error(await client.GetAsync(path), 401);
    }
    // Unsupported parameters cannot introduce hidden filters, export, sort or aggregation windows.
    [Theory] [InlineData("/api/logs?search=PRIVATE")] [InlineData("/api/logs?sortBy=password")] [InlineData("/api/logs?limit=1&skip=2")]
    [InlineData("/api/logs?export=csv")] [InlineData("/api/dashboard?window=7days")] [InlineData("/api/dashboard?country[$ne]=PRIVATE")]
    public async Task Query_controls_are_400(string path)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await Login(app, client); await Error(await client.GetAsync(path), 400);
    }
    // An empty record collection returns a truthful empty logs page and empty audit/country summaries.
    [Fact]
    public async Task Empty_activity_is_not_sample_data()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await Login(app, client); var store = app.Service<MongoStore>();
        await store.Logs.DeleteManyAsync(FilterDefinition<AuditEvent>.Empty);
        var logs = await client.GetAsync("/api/logs"); Assert.Equal(200, (int)logs.StatusCode); Assert.Empty((await logs.Content.ReadFromJsonAsync<AuditPage>())!.Items);
        var dashboard = await client.GetAsync("/api/dashboard"); Assert.Equal(200, (int)dashboard.StatusCode); var result = (await dashboard.Content.ReadFromJsonAsync<DashboardView>())!;
        Assert.Equal(2, result.Total); Assert.Empty(result.RecentLogins); Assert.Empty(result.AuditSummary); Assert.Empty(result.CountryDistribution);
    }
    // Inconsistent persisted status prevents a successful dashboard with misleading counts.
    [Fact]
    public async Task Invalid_source_status_returns_503_not_partial_dashboard()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var client = app.CreateClient(); await Login(app, client); var store = app.Service<MongoStore>();
        await store.Database.GetCollection<BsonDocument>("users").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "username", "synthetic.invalid" }, { "is_active", "PRIVATE" } });
        await Error(await client.GetAsync("/api/dashboard"), 503);
    }
    // A real lost dependency yields only a safe 503, even after a successful activity read.
    [Theory] [InlineData("/api/logs")] [InlineData("/api/dashboard")]
    public async Task Dependency_failure_is_503_without_stale_values(string path)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; using var host = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
            services.RemoveAll<UserRepository>(); services.AddTransient<UserRepository>(); services.RemoveAll<AuditRepository>(); services.AddTransient<AuditRepository>(); }));
        using var client = host.CreateClient(); await Login(app, client);
        Assert.Equal(200, (int)(await client.GetAsync(path)).StatusCode); app.DependencyUnavailable = true; app.Settings["Mongo:TimeoutSeconds"] = "1";
        try { await Error(await client.GetAsync(path), 503); }
        finally { app.DependencyUnavailable = false; app.Settings["Mongo:TimeoutSeconds"] = "10"; }
    }
}
