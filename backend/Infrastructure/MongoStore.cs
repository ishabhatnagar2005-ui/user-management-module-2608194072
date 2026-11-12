// Own one pooled Mongo client and require transactions for coordinated writes.
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Domain;
namespace UserManagement.Infrastructure;
public sealed class MongoStore
{
    public MongoClient Client { get; }
    public IMongoDatabase Database { get; }
    public IMongoCollection<UserRecord> Users { get; }
    public IMongoCollection<AuditEvent> Logs { get; }
    // Load a separate database name and finite dependency timeouts.
    public MongoStore(IConfiguration config)
    {
        var uri = config["Mongo:Uri"];
        var name = config["Mongo:Database"];
        if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsLetterOrDigit(c) && c != '_')) throw new InvalidOperationException("Supply Mongo__Uri and a safe Mongo__Database.");
        var timeout = config.GetValue<int>("Mongo:TimeoutSeconds");
        if (timeout is < 1 or > 30) throw new InvalidOperationException("Mongo__TimeoutSeconds must be between 1 and 30.");
        var settings = MongoClientSettings.FromConnectionString(uri);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(timeout);
        settings.ConnectTimeout = TimeSpan.FromSeconds(timeout);
        settings.SocketTimeout = TimeSpan.FromSeconds(timeout);
        settings.RetryWrites = false;
        Client = new MongoClient(settings);
        Database = Client.GetDatabase(name, new MongoDatabaseSettings { WriteConcern = WriteConcern.WMajority });
        Users = Database.GetCollection<UserRecord>("users");
        Logs = Database.GetCollection<AuditEvent>("logs");
    }
    // Confirm the dependency is reachable; do not fabricate readiness from process state.
    public async Task PingAsync(CancellationToken ct) => await Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
    // Execute a caller-supplied coordinated write with snapshot reads and majority commit.
    // Notes: Transaction failures propagate; there is no non-atomic fallback.
    public async Task<T> TransactionAsync<T>(Func<IClientSessionHandle, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var session = await Client.StartSessionAsync(cancellationToken: ct);
        session.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        try { var result = await operation(session, ct); await session.CommitTransactionAsync(ct); return result; }
        catch { if (session.IsInTransaction) await session.AbortTransactionAsync(CancellationToken.None); throw; }
    }
}
