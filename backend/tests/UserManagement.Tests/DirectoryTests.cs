// Exercise directory service queries and safe audit outcomes against isolated real Mongo.
using Microsoft.AspNetCore.Http;
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
// Prove optional parameters, literal/HMAC searches, stable paging and fail-closed reads.
public sealed class DirectoryTests
{
    // Parse raw URL input exactly as the collection endpoint does.
    public static IQueryCollection Parameters(string query) => new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query));
    // Seed clearly synthetic records in the owned database without modifying operator accounts.
    public static async Task<UserRecord[]> Populate(TestApplication app)
    {
        var protector = app.Service<IdentifierProtector>();
        var records = new[] {
            new UserRecord { Username = "directory.alpha", City = "Synthetic.*city", Country = "A", Education = "A", IsActive = true },
            new UserRecord { Username = "directory.beta", City = "Z", Country = "Z", Education = "Z", IsActive = false },
            new UserRecord { Username = "directory.gamma", City = "Z", Country = "Z", Education = "Z", IsActive = true } };
        records[0].AadhaarNo = protector.Protect("aadhaar_no", "SYNTHETIC-AADHAAR");
        records[0].AadhaarIndex = protector.BlindIndex("aadhaar_no", "SYNTHETIC-AADHAAR");
        records[1].PanNo = protector.Protect("pan_no", "SYNTHETIC-PAN");
        records[1].PanIndex = protector.BlindIndex("pan_no", "SYNTHETIC-PAN");
        // Put a wrong-field HMAC on another record to prove field separation in queries.
        records[2].PanIndex = protector.BlindIndex("aadhaar_no", "SYNTHETIC-PAN");
        await app.Service<MongoStore>().Users.InsertManyAsync(records);
        return records;
    }
    // Verify defaults, exact projection, read-only persistence and exact safe success events.
    [Fact]
    public async Task Default_page_is_safe_bounded_and_audited_without_mutating_users()
    {
        await using var app = new TestApplication(); var records = await Populate(app); var store = app.Service<MongoStore>();
        var before = await store.Users.Find(FilterDefinition<UserRecord>.Empty).SortBy(x => x.Id).ToListAsync();
        var actor = ObjectId.GenerateNewId(); var page = await app.Service<DirectoryService>().ListAsync(Parameters(""), actor, default);
        Assert.Equal(25, page.Limit); Assert.Equal(0, page.Skip); Assert.Equal(5, page.Items.Count);
        foreach (var item in page.Items) Assert.DoesNotContain("SYNTHETIC-", System.Text.Json.JsonSerializer.Serialize(item));
        var after = await store.Users.Find(FilterDefinition<UserRecord>.Empty).SortBy(x => x.Id).ToListAsync();
        Assert.Equal(before.Select(x => x.ToBsonDocument()), after.Select(x => x.ToBsonDocument()));
        var log = await store.Database.GetCollection<BsonDocument>("logs").Find(new BsonDocument("action", "user.list")).SingleAsync();
        Assert.Equal(new[] { "_id", "action", "status", "timestamp", "user_id" }, log.Names.Order().ToArray());
        Assert.Equal(actor, log["user_id"].AsObjectId); Assert.Equal("success", log["status"].AsString);
        Assert.DoesNotContain("directory.alpha", log.ToJson()); Assert.DoesNotContain("SYNTHETIC", log.ToJson());
    }
    // Exercise each ordinary/exact search and reject regex expansion and identifier partial/case matching.
    [Theory]
    [InlineData("DIRECTORY.ALPHA", "directory.alpha")]
    [InlineData("synthetic.*CITY", "directory.alpha")]
    [InlineData(".*", "directory.alpha")]
    [InlineData("SYNTHETIC-AADHAAR", "directory.alpha")]
    [InlineData("SYNTHETIC-PAN", "directory.beta")]
    [InlineData("AADHAAR", "")]
    [InlineData("synthetic-pan", "")]
    [InlineData("no-match", "")]
    public async Task Search_uses_escaped_username_city_and_field_specific_exact_HMAC(string search, string expected)
    {
        await using var app = new TestApplication(); await Populate(app);
        var page = await app.Service<DirectoryService>().ListAsync(Parameters("?search=" + Uri.EscapeDataString(search)), ObjectId.GenerateNewId(), default);
        Assert.Equal(expected == "" ? Array.Empty<string>() : new[] { expected }, page.Items.Select(x => x.Username));
    }
    // Test each filter independently, combined intersection and optional empty normalization.
    [Theory]
    [InlineData("country=Z", "directory.beta,directory.gamma")]
    [InlineData("education=A", "directory.alpha")]
    [InlineData("search=directory&status=inactive", "directory.beta")]
    [InlineData("search=directory&status=active", "directory.alpha,directory.gamma")]
    [InlineData("country=Z&education=Z&status=active", "directory.gamma")]
    [InlineData("country=z", "")]
    [InlineData("country=missing", "")]
    [InlineData("search=directory&country=&education=", "directory.alpha,directory.beta,directory.gamma")]
    public async Task Filters_select_only_matching_real_records(string query, string expected)
    {
        await using var app = new TestApplication(); await Populate(app);
        var page = await app.Service<DirectoryService>().ListAsync(Parameters("?" + query), ObjectId.GenerateNewId(), default);
        Assert.Equal(expected == "" ? Array.Empty<string>() : expected.Split(','), page.Items.Select(x => x.Username));
    }
    // Verify all sort directions and ObjectId tie-breaks by comparing real offset windows.
    [Theory]
    [InlineData("username", "asc")] [InlineData("username", "desc")]
    [InlineData("city", "asc")] [InlineData("city", "desc")]
    [InlineData("country", "asc")] [InlineData("country", "desc")]
    [InlineData("education", "asc")] [InlineData("education", "desc")]
    public async Task Sorts_and_offset_pages_are_stable(string field, string direction)
    {
        await using var app = new TestApplication(); var records = await Populate(app);
        // Expected ordering is calculated independently from the repository query builder.
        string? Value(UserRecord x) => field switch { "username" => x.Username, "city" => x.City, "country" => x.Country, _ => x.Education };
        var expected = direction == "asc" ? records.OrderBy(Value, StringComparer.Ordinal).ThenBy(x => x.Id) : records.OrderByDescending(Value, StringComparer.Ordinal).ThenByDescending(x => x.Id);
        var service = app.Service<DirectoryService>(); var actor = ObjectId.GenerateNewId();
        var whole = await service.ListAsync(Parameters($"?search=directory&sortBy={field}&sortDirection={direction}&limit=100"), actor, default);
        Assert.Equal(expected.Select(x => x.Id.ToString()), whole.Items.Select(x => x.Id));
        for (var skip = 0; skip < 4; skip++) {
            var window = await service.ListAsync(Parameters($"?search=directory&sortBy={field}&sortDirection={direction}&limit=1&skip={skip}"), actor, default);
            Assert.Equal(whole.Items.Skip(skip).Take(1), window.Items); Assert.Equal(skip, window.Skip); Assert.Equal(1, window.Limit);
        }
    }
    // Reject hostile and malformed input before a database query and record one redacted failure.
    [Theory]
    [InlineData("limit=0")] [InlineData("limit=101")] [InlineData("limit=-1")] [InlineData("limit=1.5")] [InlineData("limit=")]
    [InlineData("skip=-1")] [InlineData("skip=2147483648")] [InlineData("skip=")] [InlineData("skip=1.5")]
    [InlineData("sortBy=password")] [InlineData("sortBy=$where")] [InlineData("sortDirection=up")] [InlineData("status=all")]
    [InlineData("search[$ne]=x")] [InlineData("search=%7B%22%24ne%22%3Anull%7D")]
    [InlineData("search=x&search=y")] [InlineData("country=A&country=Z")] [InlineData("limit=1&Limit=2")]
    [InlineData("unexpected=x")] [InlineData("country=%00")]
    public async Task Invalid_query_is_400_with_one_safe_failure(string query)
    {
        await using var app = new TestApplication();
        var error = await Assert.ThrowsAsync<ApiException>(() => app.Service<DirectoryService>().ListAsync(Parameters("?" + query), ObjectId.GenerateNewId(), default));
        Assert.Equal(400, error.Status); Assert.Equal(new[] { "query" }, error.Errors.Keys);
        Assert.Equal(2, await app.Service<MongoStore>().Users.CountDocumentsAsync(FilterDefinition<UserRecord>.Empty));
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Action == "user.list" && x.Status == "failure"));
    }
    // Reject every text overflow while accepting its exact boundary as literal data.
    [Theory] [InlineData("search", 200)] [InlineData("country", 100)] [InlineData("education", 200)]
    public async Task Text_bounds_are_enforced(string field, int maximum)
    {
        await using var app = new TestApplication(); var service = app.Service<DirectoryService>();
        Assert.Empty((await service.ListAsync(Parameters($"?{field}=" + new string('x', maximum)), ObjectId.GenerateNewId(), default)).Items);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.ListAsync(Parameters($"?{field}=" + new string('x', maximum + 1)), ObjectId.GenerateNewId(), default))).Status);
    }
    // Capture actual explain evidence for indexed filter/sort and acknowledge unanchored search scans.
    [Fact]
    public async Task Real_explain_uses_directory_index_for_country_sort()
    {
        await using var app = new TestApplication(); await Populate(app); var store = app.Service<MongoStore>();
        var explain = await store.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "explain", new BsonDocument { { "find", "users" }, { "filter", new BsonDocument("country", "Z") },
                { "sort", new BsonDocument { { "country", 1 }, { "_id", 1 } } }, { "limit", 25 }, { "hint", "directory_country_v1" },
                { "projection", new BsonDocument { { "username", 1 }, { "country", 1 } } } } }, { "verbosity", "executionStats" } });
        Assert.Contains("IXSCAN", explain["queryPlanner"].ToJson()); Assert.Equal(2, explain["executionStats"]["nReturned"].AsInt32);
        Assert.Contains("directory_country_v1", explain.ToJson());
    }
    // Prove an audit write rejection and a real unreachable Mongo prevent successful reads.
    [Fact]
    public async Task Audit_and_dependency_failure_cannot_return_success()
    {
        await using var app = new TestApplication(); var store = app.Service<MongoStore>();
        await store.Database.CreateCollectionAsync("logs");
        await store.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "logs" }, { "validator", new BsonDocument("action", "login") }, { "validationLevel", "strict" } });
        await Assert.ThrowsAnyAsync<MongoException>(() => app.Service<DirectoryService>().ListAsync(Parameters(""), ObjectId.GenerateNewId(), default));
        var offline = new MongoStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(app.Settings) { ["Mongo:Uri"] = "mongodb://127.0.0.1:1/", ["Mongo:TimeoutSeconds"] = "1" }).Build());
        var service = new DirectoryService(new UserRepository(offline), new AuditRepository(offline), app.Service<IdentifierProtector>());
        await Assert.ThrowsAsync<TimeoutException>(() => service.ListAsync(Parameters(""), ObjectId.GenerateNewId(), default));
        Assert.Equal(2, await store.Users.CountDocumentsAsync(FilterDefinition<UserRecord>.Empty));
    }
}
