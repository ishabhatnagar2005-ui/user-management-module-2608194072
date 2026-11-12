// Provide reusable real Mongo operations without defining domain endpoints.
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Domain;
namespace UserManagement.Infrastructure;
public sealed class UserRepository(MongoStore store)
{
    // Read an exact-case unique username for authentication.
    public Task<UserRecord?> FindByUsernameAsync(string username, CancellationToken ct) => FindAsync(Builders<UserRecord>.Filter.Eq(x => x.Username, username), null, ct);
    // Read an identity with optional transaction context.
    public Task<UserRecord?> FindByIdAsync(ObjectId id, CancellationToken ct, IClientSessionHandle? session = null) => FindAsync(Builders<UserRecord>.Filter.Eq(x => x.Id, id), session, ct);
    // Restrict filters to typed expressions supplied by application services.
    private async Task<UserRecord?> FindAsync(FilterDefinition<UserRecord> filter, IClientSessionHandle? session, CancellationToken ct) =>
        session is null ? await store.Users.Find(filter).FirstOrDefaultAsync(ct) : await store.Users.Find(session, filter).FirstOrDefaultAsync(ct);
    // Insert an already-validated hashed/encrypted record in a caller's transaction.
    public Task InsertAsync(UserRecord record, CancellationToken ct, IClientSessionHandle? session = null) =>
        session is null ? store.Users.InsertOneAsync(record, cancellationToken: ct) : store.Users.InsertOneAsync(session, record, cancellationToken: ct);
    // Persist validated update operators and return the post-write document.
    public Task<UserRecord> UpdateAsync(FilterDefinition<UserRecord> filter, UpdateDefinition<UserRecord> update, IClientSessionHandle session, CancellationToken ct) =>
        store.Users.FindOneAndUpdateAsync(session, filter, update, new FindOneAndUpdateOptions<UserRecord> { ReturnDocument = ReturnDocument.After }, ct);
    // Query within explicit bounds and stable server-side ordering.
    public Task<List<UserRecord>> QueryAsync(FilterDefinition<UserRecord> filter, SortDefinition<UserRecord> sort, int limit, int skip, CancellationToken ct)
    {
        if (limit is < 1 or > 100 || skip < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        return store.Users.Find(filter).Sort(sort).Skip(skip).Limit(limit).ToListAsync(ct);
    }
    // Upsert operator-owned seed data without modifying existing credentials or role.
    public Task<UpdateResult> SeedAsync(UserRecord record, CancellationToken ct) => store.Users.UpdateOneAsync(
        Builders<UserRecord>.Filter.Eq(x => x.Username, record.Username),
        Builders<UserRecord>.Update.SetOnInsert(x => x.Id, record.Id).SetOnInsert(x => x.Username, record.Username)
            .SetOnInsert(x => x.PasswordHash, record.PasswordHash).SetOnInsert(x => x.Role, record.Role)
            .SetOnInsert(x => x.CredentialVersion, record.CredentialVersion).SetOnInsert(x => x.IsActive, record.IsActive)
            .SetOnInsert(x => x.CreatedAt, record.CreatedAt).SetOnInsert(x => x.UpdatedAt, record.UpdatedAt),
        new UpdateOptions { IsUpsert = true }, ct);
}
