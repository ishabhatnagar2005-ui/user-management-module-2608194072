// Durably persist exact audit documents through the application's pooled Mongo store.
using MongoDB.Driver;
using UserManagement.Domain;
using UserManagement.Infrastructure;
namespace UserManagement.Audit;
public sealed class AuditRepository(MongoStore store)
{
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
