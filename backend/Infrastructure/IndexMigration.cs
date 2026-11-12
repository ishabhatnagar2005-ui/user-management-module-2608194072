// Apply repeatable username uniqueness after a collision preflight; never delete data/indexes.
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Domain;
namespace UserManagement.Infrastructure;
public sealed class IndexMigration(MongoStore store)
{
    // Fail deployment visibly when existing data violates exact-case uniqueness.
    public async Task ApplyAsync(CancellationToken ct)
    {
        var duplicates = await store.Users.Aggregate<BsonDocument>(new[] {
            new BsonDocument("$group", new BsonDocument { { "_id", "$username" }, { "count", new BsonDocument("$sum", 1) } }),
            new BsonDocument("$match", new BsonDocument("count", new BsonDocument("$gt", 1))),
            new BsonDocument("$limit", 1)
        }).ToListAsync(ct);
        if (duplicates.Count > 0) throw new InvalidOperationException("Resolve username collisions before applying the unique index.");
        await store.Users.Indexes.CreateOneAsync(new CreateIndexModel<UserRecord>(Builders<UserRecord>.IndexKeys.Ascending(x => x.Username),
            new CreateIndexOptions { Name = "username_unique_v1", Unique = true, Collation = new Collation("simple") }), cancellationToken: ct);
    }
}
