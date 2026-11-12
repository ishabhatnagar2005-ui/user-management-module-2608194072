// Provide reusable real Mongo operations without defining domain endpoints.
using System.Text.RegularExpressions;
using UserManagement.Api;
using UserManagement.Application;
using UserManagement.Security;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Domain;
namespace UserManagement.Infrastructure;
// Reuse the pooled collection for validated persistence and server-side directory projection.
public sealed class UserRepository(MongoStore store)
{
    // Aggregate statuses and populated countries in one facet within the caller's snapshot.
    // Notes: Missing or non-boolean status stays in total, forcing reconciliation failure rather than invented classification.
    public Task<BsonDocument> ActivityCountsAsync(IClientSessionHandle session, CancellationToken ct)
    {
        BsonDocument[] pipeline = [BsonDocument.Parse("""
            { "$facet": {
              "counts": [{ "$group": { "_id": null, "total": { "$sum": 1 },
                "active": { "$sum": { "$cond": [{ "$and": [{ "$eq": [{ "$type": "$is_active" }, "bool"] }, { "$eq": ["$is_active", true] }] }, 1, 0] } },
                "inactive": { "$sum": { "$cond": [{ "$and": [{ "$eq": [{ "$type": "$is_active" }, "bool"] }, { "$eq": ["$is_active", false] }] }, 1, 0] } } } }],
              "countries": [{ "$match": { "$expr": { "$eq": [{ "$type": "$country" }, "string"] }, "country": { "$regex": "\\S" } } },
                { "$group": { "_id": "$country", "count": { "$sum": 1 } } }, { "$sort": { "_id": 1 } }]
            } }
            """)];
        return store.Database.GetCollection<BsonDocument>("users").Aggregate<BsonDocument>(session, pipeline).SingleAsync(ct);
    }
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
    // Prevent stale credentials or a concurrently deactivated account from accepting a profile write.
    // Returns: The updated record, or null when the active credential snapshot no longer matches.
    public Task<UserRecord?> UpdateCredentialConditionalAsync(ObjectId id, long version, UpdateDefinition<UserRecord> update,
        IClientSessionHandle session, CancellationToken ct) => store.Users.FindOneAndUpdateAsync<UserRecord?>(session,
            Builders<UserRecord>.Filter.Eq(x => x.Id, id) & Builders<UserRecord>.Filter.Eq(x => x.IsActive, true) &
            Builders<UserRecord>.Filter.Eq(x => x.CredentialVersion, version), update,
            new FindOneAndUpdateOptions<UserRecord, UserRecord?> { ReturnDocument = ReturnDocument.After }, ct);
    // Query within explicit bounds and stable server-side ordering.
    public Task<List<UserRecord>> QueryAsync(FilterDefinition<UserRecord> filter, SortDefinition<UserRecord> sort, int limit, int skip, CancellationToken ct)
    {
        if (limit is < 1 or > 100 || skip < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        return store.Users.Find(filter).Sort(sort).Skip(skip).Limit(limit).ToListAsync(ct);
    }
    // Create repeatable indexes for exact filters and four stable sorts without deleting existing data.
    // Notes: Unanchored case-insensitive literal search may scan; no performance SLO is promised.
    public Task EnsureDirectoryIndexesAsync(CancellationToken ct)
    {
        var keys = Builders<UserRecord>.IndexKeys;
        var indexes = new List<CreateIndexModel<UserRecord>> {
            new(keys.Ascending(x => x.AadhaarIndex), new CreateIndexOptions { Name = "directory_aadhaar_v1" }),
            new(keys.Ascending(x => x.PanIndex), new CreateIndexOptions { Name = "directory_pan_v1" }),
            new(keys.Ascending(x => x.IsActive).Ascending(x => x.Id), new CreateIndexOptions { Name = "directory_status_v1" }) };
        foreach (var field in new[] { "username", "city", "country", "education" })
            indexes.Add(new(keys.Ascending(field).Ascending(x => x.Id), new CreateIndexOptions { Name = "directory_" + field + "_v1" }));
        return store.Users.Indexes.CreateManyAsync(indexes, cancellationToken: ct);
    }
    // Construct operators only from an immutable validated query and field-specific HMAC values.
    // Returns: A server-side filter; regex syntax in ordinary search remains escaped literal text.
    public FilterDefinition<UserRecord> DirectoryFilter(UserQuery query, IdentifierProtector protector)
    {
        var f = Builders<UserRecord>.Filter;
        var filter = f.Empty;
        if (query.Search is { } search)
        {
            var literal = new BsonRegularExpression(Regex.Escape(search), "i");
            filter &= f.Or(f.Regex(x => x.Username, literal), f.Regex(x => x.City, literal),
                f.Eq(x => x.AadhaarIndex, protector.BlindIndex("aadhaar_no", search)), f.Eq(x => x.PanIndex, protector.BlindIndex("pan_no", search)));
        }
        if (query.Country is { } country) filter &= f.Eq(x => x.Country, country);
        if (query.Education is { } education) filter &= f.Eq(x => x.Education, education);
        if (query.Active is { } active) filter &= f.Eq(x => x.IsActive, active);
        return filter;
    }
    // Order by an allowlisted field and ObjectId in the same direction for stable offset pages.
    public SortDefinition<UserRecord> DirectorySort(UserQuery query) => query.Descending
        ? Builders<UserRecord>.Sort.Descending(query.SortBy).Descending(x => x.Id)
        : Builders<UserRecord>.Sort.Ascending(query.SortBy).Ascending(x => x.Id);
    // Project only public fields in Mongo before materializing a bounded result.
    // Notes: Neither ciphertext, blind indexes nor credentials are fetched or decrypted.
    public async Task<List<UserView>> ListDirectoryAsync(UserQuery query, IdentifierProtector protector, CancellationToken ct)
    {
        var projection = Builders<UserRecord>.Projection.Expression(x => new UserView(x.Id.ToString(), x.Username,
            x.Address, x.City, x.Country, x.Education, x.IsActive, x.CreatedAt, x.UpdatedAt));
        return await store.Users.Find(DirectoryFilter(query, protector)).Sort(DirectorySort(query))
            .Skip(query.Skip).Limit(query.Limit).Project(projection).ToListAsync(ct);
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
