// Verify reconciled source summaries from real Mongo snapshot queries.
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api;
using UserManagement.Application;
using UserManagement.Domain;
using UserManagement.Infrastructure;
using Xunit;
namespace UserManagement.Tests;
// Exercise status reconciliation, country exclusions and full audit grouping independently of HTTP.
public sealed class DashboardTests
{
    // Compute all six summaries from persisted records, including old login timestamps and both outcomes.
    [Fact]
    public async Task Summaries_reconcile_and_use_full_source_not_display_window()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; var store = app.Service<MongoStore>();
        await store.Users.InsertManyAsync([new() { Username = "country.one", Country = "Country A" }, new() { Username = "country.two", Country = "Country A", IsActive = false }, new() { Username = "country.three", Country = "Country B" }]);
        var records = Enumerable.Range(0, 15).Select(i => new AuditEvent { Action = "login", Status = i % 2 == 0 ? "success" : "failure", Timestamp = DateTime.UtcNow.AddDays(-400).AddMinutes(i) }).ToList();
        records.Add(AuditEvent.Create(null, "profile.update", true)); await store.Logs.InsertManyAsync(records);
        var result = await app.Service<DashboardService>().ReadAsync(QueryCollection.Empty, default);
        Assert.Equal(5, result.Total); Assert.Equal(4, result.Active); Assert.Equal(1, result.Inactive); Assert.Equal(result.Total, result.Active + result.Inactive);
        Assert.Equal(new[] { new CountryCount("Country A", 2), new CountryCount("Country B", 1) }, result.CountryDistribution);
        Assert.Equal(records.Where(x => x.Action == "login").OrderByDescending(x => x.Timestamp).Take(10).Select(x => x.Id.ToString()), result.RecentLogins.Select(x => x.Id));
        Assert.Equal(new[] { new AuditActivity("login", 8, 7), new AuditActivity("profile.update", 1, 0) }, result.AuditSummary);
        Assert.Equal(16, await store.Logs.CountDocumentsAsync(FilterDefinition<AuditEvent>.Empty));
    }
    // Missing, null, blank, whitespace and non-string countries must not become distribution buckets.
    [Theory] [InlineData("missing")] [InlineData("null")] [InlineData("empty")] [InlineData("space")] [InlineData("number")] [InlineData("array")]
    public async Task Country_distribution_excludes_unpopulated_values(string kind)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; var store = app.Service<MongoStore>();
        var user = new UserRecord { Username = "synthetic.country" }.ToBsonDocument();
        if (kind != "missing") user["country"] = kind switch { "null" => BsonNull.Value, "empty" => "", "space" => "   ", "number" => 12, _ => new BsonArray { "Country" } };
        await store.Database.GetCollection<BsonDocument>("users").InsertOneAsync(user);
        var result = await app.Service<DashboardService>().ReadAsync(QueryCollection.Empty, default); Assert.Equal(3, result.Total); Assert.Empty(result.CountryDistribution);
    }
    // Corrupt lifecycle states cannot silently default to active or produce unreconciled counts.
    [Theory] [InlineData("missing")] [InlineData("null")] [InlineData("string")] [InlineData("number")] [InlineData("array")]
    public async Task Inconsistent_source_status_is_safe_503(string kind)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; var store = app.Service<MongoStore>(); var user = new UserRecord { Username = "synthetic.invalid" }.ToBsonDocument();
        if (kind == "missing") user.Remove("is_active"); else user["is_active"] = kind switch { "null" => BsonNull.Value, "string" => "true", "number" => 1, _ => new BsonArray { true } };
        await store.Database.GetCollection<BsonDocument>("users").InsertOneAsync(user);
        var error = await Assert.ThrowsAsync<ApiException>(() => app.Service<DashboardService>().ReadAsync(QueryCollection.Empty, default)); Assert.Equal(503, error.Status);
    }
    // Unsupported legacy actions/statuses/dates cannot create summary categories or recent outcomes.
    [Theory] [InlineData("action")] [InlineData("status")] [InlineData("timestamp")]
    public async Task Audit_summary_uses_approved_records_only(string field)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; var store = app.Service<MongoStore>(); var record = AuditEvent.Create(null, "login", true).ToBsonDocument(); record[field] = "SYNTHETIC_PRIVATE";
        await store.Database.GetCollection<BsonDocument>("logs").InsertOneAsync(record);
        var result = await app.Service<DashboardService>().ReadAsync(QueryCollection.Empty, default); Assert.Empty(result.AuditSummary); Assert.Empty(result.RecentLogins);
    }
    // A truly empty database returns zeros and empty collections, not fake samples.
    [Fact]
    public async Task Empty_sources_return_zero_and_empty()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; app.Settings["Seed:Enabled"] = "false";
        var result = await app.Service<DashboardService>().ReadAsync(QueryCollection.Empty, default); Assert.Equal(0, result.Total); Assert.Equal(0, result.Active); Assert.Equal(0, result.Inactive);
        Assert.Empty(result.CountryDistribution); Assert.Empty(result.RecentLogins); Assert.Empty(result.AuditSummary);
    }
    // Dashboard query controls are not part of the approved read contract.
    [Fact]
    public async Task Unknown_query_is_rejected()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => app.Service<DashboardService>().ReadAsync(new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>() { ["window"] = "7days" }), default))).Status);
    }
    // A lost Mongo dependency must not return earlier counts or partial collections.
    [Fact]
    public async Task Dependency_failure_propagates_without_stale_summary()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; _ = app.Service<MongoStore>();
        var settings = new Dictionary<string, string?>(app.Settings) { ["Mongo:Uri"] = "mongodb://127.0.0.1:1/", ["Mongo:TimeoutSeconds"] = "1" };
        var offline = new MongoStore(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var service = new DashboardService(offline, new UserRepository(offline), new UserManagement.Audit.AuditRepository(offline));
        await Assert.ThrowsAsync<TimeoutException>(() => service.ReadAsync(QueryCollection.Empty, default));
    }
}
