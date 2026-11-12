// Durably persist exact audit documents through the application's pooled Mongo store.
using MongoDB.Driver;
using MongoDB.Bson;
using UserManagement.Application;
using UserManagement.Domain;
using UserManagement.Infrastructure;
namespace UserManagement.Audit;
public sealed class AuditRepository(MongoStore store)
{
    // Match only approved scalar audit vocabulary and valid stored source timestamps.
    public static BsonDocument ApprovedFilter() => BsonDocument.Parse("""
        { "_id": { "$type": "objectId" }, "action": { "$type": "string", "$in": ["login", "user.create", "user.read", "user.list", "user.update", "user.deactivate", "profile.update", "password.change"] },
          "status": { "$type": "string", "$in": ["success", "failure"] }, "timestamp": { "$type": "date" },
          "$expr": { "$and": [ { "$eq": [{ "$type": "$action" }, "string"] }, { "$eq": [{ "$type": "$status" }, "string"] }, { "$eq": [{ "$type": "$timestamp" }, "date"] } ] } }
        """);
    // Fetch only safe fields, with a fixed bound and deterministic source-time ordering.
    public async Task<List<AuditView>> ReadSafeAsync(int limit, bool loginOnly, IClientSessionHandle? session, CancellationToken ct)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var collection = store.Database.GetCollection<BsonDocument>("logs");
        var filter = ApprovedFilter();
        if (loginOnly) filter["action"] = "login";
        var read = session is null ? collection.Find(filter) : collection.Find(session, filter);
        var documents = await read.Sort(new BsonDocument { { "timestamp", -1 }, { "_id", -1 } }).Limit(limit)
            .Project<BsonDocument>(new BsonDocument { { "_id", 1 }, { "user_id", 1 }, { "action", 1 }, { "status", 1 }, { "timestamp", 1 } }).ToListAsync(ct);
        return documents.Select(AuditService.Project).ToList();
    }
    // Group the full approved record set in Mongo, not the bounded display list.
    public async Task<List<AuditActivity>> SummarizeAsync(IClientSessionHandle session, CancellationToken ct)
    {
        BsonDocument[] pipeline = [new("$match", ApprovedFilter()), BsonDocument.Parse("""
            { "$group": { "_id": "$action", "success": { "$sum": { "$cond": [{ "$eq": ["$status", "success"] }, 1, 0] } },
              "failure": { "$sum": { "$cond": [{ "$eq": ["$status", "failure"] }, 1, 0] } } } }
            """), new("$sort", new BsonDocument("_id", 1))];
        var rows = await store.Database.GetCollection<BsonDocument>("logs").Aggregate<BsonDocument>(session, pipeline).ToListAsync(ct);
        return rows.Select(x => new AuditActivity(x["_id"].AsString, x["success"].ToInt64(), x["failure"].ToInt64())).ToList();
    }
    // Acknowledge a majority write before reporting an authentication outcome.
    public Task RecordAsync(AuditEvent audit, CancellationToken ct, IClientSessionHandle? session = null) =>
        session is null ? store.Logs.InsertOneAsync(audit, cancellationToken: ct) : store.Logs.InsertOneAsync(session, audit, cancellationToken: ct);
    // Read bounded recent records with a stable identity tie-break; no payload metadata exists.
    public Task<List<AuditEvent>> ReadAsync(FilterDefinition<AuditEvent> filter, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return store.Logs.Find(filter).Sort(Builders<AuditEvent>.Sort.Descending(x => x.Timestamp).Descending(x => x.Id)).Limit(limit).ToListAsync(ct);
    }
}
