// Verify strict collection HTTP contracts, denial audits and every optional parameter on real Mongo.
using System.Net;
using System.Net.Http.Json;
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
using Xunit;
namespace UserManagement.Tests;
// Exercise the actual controller, authentication pipeline and safe persistence projections.
public sealed class DirectoryApiTests
{
    // Sign in through the production endpoint rather than fabricating bearer credentials.
    private static async Task SignIn(TestApplication app, HttpClient client, string username = "test.admin")
    {
        var response = await client.PostAsJsonAsync("/api/login", new LoginRequest(username, app.Password)); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
    }
    // Assert exactly the approved envelope and public user projection, without a total-count promise.
    private static async Task<JsonElement> Page(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "items", "limit", "skip" }, page.EnumerateObject().Select(x => x.Name).Order());
        foreach (var item in page.GetProperty("items").EnumerateArray())
            Assert.Equal(new[] { "address", "city", "country", "created_at", "education", "id", "is_active", "updated_at", "username" }, item.EnumerateObject().Select(x => x.Name).Order());
        Assert.DoesNotContain("SYNTHETIC-AADHAAR", page.ToString()); Assert.DoesNotContain("SYNTHETIC-PAN", page.ToString());
        return page;
    }
    // Assert redacted error shape and exact status, not only a failing HTTP code.
    private static async Task Error(HttpResponseMessage response, int status)
    {
        Assert.Equal(status, (int)response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "correlationId", "errors", "status", "title", "type" }, body.EnumerateObject().Select(x => x.Name).Order());
        Assert.Equal(status, body.GetProperty("status").GetInt32()); Assert.NotEmpty(body.GetProperty("correlationId").GetString()!);
        Assert.DoesNotContain("$ne", body.ToString()); Assert.DoesNotContain("mongodb", body.ToString());
    }
    // Verify defaults and inclusive numeric bounds including an empty far-offset page.
    [Theory] [InlineData("", 25, 0, 5)] [InlineData("?limit=1", 1, 0, 1)] [InlineData("?limit=100&skip=0", 100, 0, 5)] [InlineData("?skip=2147483647", 25, 2147483647, 0)]
    public async Task Default_and_boundary_pages_have_safe_envelope(string query, int limit, int skip, int count)
    {
        await using var app = new TestApplication(); using var client = app.CreateClient(); await SignIn(app, client); await DirectoryTests.Populate(app);
        var page = await Page(await client.GetAsync("/api/users" + query));
        Assert.Equal(limit, page.GetProperty("limit").GetInt32()); Assert.Equal(skip, page.GetProperty("skip").GetInt32()); Assert.Equal(count, page.GetProperty("items").GetArrayLength());
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "user.list" && x.Status == "success"));
    }
    // Exercise each search/filter option, literal punctuation, exact-case identifiers and empty filters.
    [Theory]
    [InlineData("search=DIRECTORY.ALPHA", "directory.alpha")]
    [InlineData("search=synthetic.*CITY", "directory.alpha")]
    [InlineData("search=.*", "directory.alpha")]
    [InlineData("search=SYNTHETIC-AADHAAR", "directory.alpha")]
    [InlineData("search=SYNTHETIC-PAN", "directory.beta")]
    [InlineData("search=synthetic-pan", "")]
    [InlineData("search=AADHAAR", "")]
    [InlineData("country=Z", "directory.beta,directory.gamma")]
    [InlineData("education=A", "directory.alpha")]
    [InlineData("search=directory&status=active", "directory.alpha,directory.gamma")]
    [InlineData("status=inactive", "directory.beta")]
    [InlineData("country=Z&education=Z&status=active", "directory.gamma")]
    [InlineData("country=missing", "")]
    [InlineData("search=directory&country=&education=", "directory.alpha,directory.beta,directory.gamma")]
    public async Task Optional_parameters_query_real_records(string query, string expected)
    {
        await using var app = new TestApplication(); using var client = app.CreateClient(); await SignIn(app, client); await DirectoryTests.Populate(app);
        var page = await Page(await client.GetAsync("/api/users?" + query));
        Assert.Equal(expected == "" ? Array.Empty<string>() : expected.Split(','), page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("username").GetString()));
    }
    // Assert all four sorts in both directions, including ObjectId tie-break and offset stability.
    [Theory]
    [InlineData("username", "asc")] [InlineData("username", "desc")] [InlineData("city", "asc")] [InlineData("city", "desc")]
    [InlineData("country", "asc")] [InlineData("country", "desc")] [InlineData("education", "asc")] [InlineData("education", "desc")]
    public async Task Sort_and_direction_are_applied_on_server(string field, string direction)
    {
        await using var app = new TestApplication(); using var client = app.CreateClient(); await SignIn(app, client); var records = await DirectoryTests.Populate(app);
        // Compute expected order from independent fixture values, not the query implementation.
        string? Value(UserRecord x) => field switch { "username" => x.Username, "city" => x.City, "country" => x.Country, _ => x.Education };
        var expected = direction == "asc" ? records.OrderBy(Value, StringComparer.Ordinal).ThenBy(x => x.Id) : records.OrderByDescending(Value, StringComparer.Ordinal).ThenByDescending(x => x.Id);
        var page = await Page(await client.GetAsync($"/api/users?search=directory&sortBy={field}&sortDirection={direction}&limit=1&skip=1"));
        Assert.Equal(expected.Skip(1).Take(1).Select(x => x.Id.ToString()), page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetString()));
    }
    // Reject unknown/duplicate parameters, operators, malformed bounds and empty enum values.
    [Theory]
    [InlineData("limit=0")] [InlineData("limit=101")] [InlineData("limit=-1")] [InlineData("limit=1.5")] [InlineData("limit=")] [InlineData("limit=no")]
    [InlineData("skip=-1")] [InlineData("skip=2147483648")] [InlineData("skip=")] [InlineData("skip=1.5")]
    [InlineData("sortBy=password")] [InlineData("sortBy=$where")] [InlineData("sortBy=")] [InlineData("sortDirection=up")] [InlineData("sortDirection=")]
    [InlineData("status=all")] [InlineData("status=")] [InlineData("country[$ne]=x")] [InlineData("search=%7B%22%24ne%22%3Anull%7D")]
    [InlineData("search=x&search=y")] [InlineData("country=A&country=Z")] [InlineData("education=A&education=Z")]
    [InlineData("limit=1&limit=2")] [InlineData("skip=0&skip=1")] [InlineData("status=active&status=inactive")]
    [InlineData("sortBy=city&sortBy=username")] [InlineData("sortDirection=asc&sortDirection=desc")] [InlineData("limit=1&Limit=2")]
    [InlineData("unexpected=x")] [InlineData("search=%00")] [InlineData("country=%24ne")] [InlineData("education=%5B%5D")]
    public async Task Invalid_query_is_safe_400_and_durable_failure(string query)
    {
        await using var app = new TestApplication(); using var client = app.CreateClient(); await SignIn(app, client);
        await Error(await client.GetAsync("/api/users?" + query), 400);
        Assert.Equal(2, await app.Service<MongoStore>().Users.CountDocumentsAsync(FilterDefinition<UserRecord>.Empty));
        var log = await app.Service<MongoStore>().Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument("action", "user.list")).SingleAsync();
        Assert.Equal("failure", log["status"].AsString); Assert.Equal(new[] { "_id", "action", "status", "timestamp", "user_id" }, log.Names.Order());
        Assert.DoesNotContain("$ne", log.ToJson()); Assert.DoesNotContain(query, log.ToJson());
    }
    // Exercise exact HTTP text bounds without permitting unbounded regex work.
    [Theory] [InlineData("search", 200)] [InlineData("country", 100)] [InlineData("education", 200)]
    public async Task Oversize_text_is_rejected_and_boundary_is_accepted(string field, int maximum)
    {
        await using var app = new TestApplication(); using var client = app.CreateClient(); await SignIn(app, client);
        Assert.Empty((await Page(await client.GetAsync($"/api/users?{field}=" + new string('x', maximum)))).GetProperty("items").EnumerateArray());
        await Error(await client.GetAsync($"/api/users?{field}=" + new string('x', maximum + 1)), 400);
    }
    // Assert 401/403 denials and omit identity for unverified requests while preserving users.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Unknown_and_User_callers_are_denied_with_safe_failure_audit(bool signedIn)
    {
        await using var app = new TestApplication(); using var client = app.CreateClient(); if (signedIn) await SignIn(app, client, "test.user");
        await Error(await client.GetAsync("/api/users?search=SYNTHETIC-PAN"), signedIn ? 403 : 401);
        var log = await app.Service<MongoStore>().Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument("action", "user.list")).SingleAsync();
        Assert.Equal(signedIn, log.Contains("user_id")); Assert.Equal(signedIn ? 5 : 4, log.ElementCount); Assert.Equal("failure", log["status"].AsString);
        Assert.DoesNotContain("SYNTHETIC", log.ToJson()); Assert.Equal(2, await app.Service<MongoStore>().Users.CountDocumentsAsync(FilterDefinition<UserRecord>.Empty));
    }
    // Reject a real audit validator failure even when collection data remains readable.
    [Fact]
    public async Task Audit_failure_returns_safe_503_and_preserves_account_state()
    {
        await using var app = new TestApplication(); using var client = app.CreateClient(); await SignIn(app, client);
        var store = app.Service<MongoStore>();
        await store.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "collMod", "logs" }, { "validator", new BsonDocument("action", "login") }, { "validationLevel", "strict" } });
        await Error(await client.GetAsync("/api/users"), 503);
        Assert.Equal(2, await store.Users.CountDocumentsAsync(FilterDefinition<UserRecord>.Empty));
        Assert.Equal(0, await store.Logs.CountDocumentsAsync(x => x.Action == "user.list"));
    }
    // Require a safe dependency response when either query/auth or denial audit loses real Mongo.
    [Fact]
    public async Task Dependency_failure_returns_503_without_fabricated_page()
    {
        await using var app = new TestApplication();
        using var host = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
            services.RemoveAll<UserRepository>(); services.AddTransient<UserRepository>(); services.RemoveAll<AuditRepository>(); services.AddTransient<AuditRepository>();
        }));
        using var client = host.CreateClient(); await SignIn(app, client); app.DependencyUnavailable = true;
        try { await Error(await client.GetAsync("/api/users"), 503); client.DefaultRequestHeaders.Authorization = null; await Error(await client.GetAsync("/api/users"), 503); }
        finally { app.DependencyUnavailable = false; }
    }
}
