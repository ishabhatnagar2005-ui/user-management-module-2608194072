// Verify safe audit reads against isolated real Mongo records, including hostile legacy content.
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api;
using UserManagement.Application;
using UserManagement.Audit;
using UserManagement.Domain;
using UserManagement.Infrastructure;
using Xunit;
namespace UserManagement.Tests;
// Exercise the actual repository projection and application validation without database mocks.
public sealed class AuditTests
{
    // Preserve timestamps and known actors, omit unavailable identity, and strip unexpected personal fields.
    [Fact]
    public async Task Read_projects_exact_safe_fields_and_does_not_write()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; var store = app.Service<MongoStore>();
        var known = AuditEvent.Create(ObjectId.GenerateNewId(), "user.update", true).ToBsonDocument(); known["address"] = "SYNTHETIC_PRIVATE";
        var unknown = AuditEvent.Create(null, "login", false).ToBsonDocument(); unknown["timestamp"] = DateTime.UtcNow.AddDays(-400); unknown["password"] = "SYNTHETIC_PRIVATE";
        await store.Database.GetCollection<BsonDocument>("logs").InsertManyAsync([unknown, known]);
        var result = await app.Service<AuditService>().ReadAsync(QueryCollection.Empty, default);
        Assert.Equal(2, result.Items.Count); Assert.Equal(known["_id"].ToString(), result.Items[0].Id);
        Assert.Equal(known["user_id"].ToString(), result.Items[0].UserId); Assert.Null(result.Items[1].UserId);
        Assert.Equal(unknown["timestamp"].ToUniversalTime(), result.Items[1].Timestamp);
        Assert.Equal("failure", result.Items[1].Status); Assert.Equal(2, await store.Logs.CountDocumentsAsync(FilterDefinition<AuditEvent>.Empty));
        Assert.DoesNotContain("SYNTHETIC_PRIVATE", System.Text.Json.JsonSerializer.Serialize(result));
    }
    // Exclude unsupported or malformed source vocabulary rather than echoing it into UI or aggregates.
    [Theory] [InlineData("action")] [InlineData("status")] [InlineData("timestamp")] [InlineData("arrayAction")]
    public async Task Invalid_legacy_record_is_not_approved(string field)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; var store = app.Service<MongoStore>();
        var record = AuditEvent.Create(null, "login", true).ToBsonDocument();
        if (field == "arrayAction") record["action"] = new BsonArray { "login" }; else record[field] = "SYNTHETIC_PRIVATE";
        await store.Database.GetCollection<BsonDocument>("logs").InsertOneAsync(record);
        Assert.Empty((await app.Service<AuditService>().ReadAsync(QueryCollection.Empty, default)).Items);
    }
    // A malformed actor is omitted without losing a valid approved outcome.
    [Fact]
    public async Task Malformed_actor_is_not_fabricated()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; var store = app.Service<MongoStore>();
        var record = AuditEvent.Create(null, "login", false).ToBsonDocument(); record["user_id"] = "SYNTHETIC_PRIVATE";
        await store.Database.GetCollection<BsonDocument>("logs").InsertOneAsync(record);
        Assert.Null(Assert.Single((await app.Service<AuditService>().ReadAsync(QueryCollection.Empty, default)).Items).UserId);
    }
    // Latest hundred are selected by source timestamp with descending ObjectId ties.
    [Fact]
    public async Task Read_is_bounded_and_stably_ordered()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; var store = app.Service<MongoStore>(); var timestamp = DateTime.UtcNow.AddDays(-60);
        var records = Enumerable.Range(0, 105).Select(_ => new AuditEvent { Action = "login", Status = "success", Timestamp = timestamp }).ToList();
        await store.Logs.InsertManyAsync(records);
        var result = await app.Service<AuditService>().ReadAsync(QueryCollection.Empty, default);
        Assert.Equal(records.OrderByDescending(x => x.Id).Take(100).Select(x => x.Id.ToString()), result.Items.Select(x => x.Id));
    }
    // Empty persistence renders an actual empty collection rather than sample outcomes.
    [Fact]
    public async Task Empty_returns_empty_items()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; Assert.Empty((await app.Service<AuditService>().ReadAsync(QueryCollection.Empty, default)).Items);
    }
    // Unsupported UI controls are rejected before source access.
    [Theory] [InlineData("search")] [InlineData("sort")] [InlineData("skip")] [InlineData("export")]
    public async Task Query_controls_are_rejected(string name)
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; var error = await Assert.ThrowsAsync<ApiException>(() => app.Service<AuditService>().ReadAsync(new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>() { [name] = "private" }), default));
        Assert.Equal(400, error.Status); Assert.DoesNotContain("private", error.Message);
    }
    // Dependency failure must propagate, never returning an earlier successful collection.
    [Fact]
    public async Task Dependency_failure_does_not_return_stale_items()
    {
        await using var app = new TestApplication(); app.Settings["Mongo:TimeoutSeconds"] = "10"; _ = app.Service<MongoStore>(); app.DependencyUnavailable = true;
        await Assert.ThrowsAsync<TimeoutException>(() => app.Service<AuditService>().ReadAsync(QueryCollection.Empty, default));
    }
}
