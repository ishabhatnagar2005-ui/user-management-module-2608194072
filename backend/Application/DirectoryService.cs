// Coordinate bounded directory reads and durable redacted audit outcomes.
using MongoDB.Bson;
using UserManagement.Api;
using UserManagement.Audit;
using UserManagement.Domain;
using UserManagement.Infrastructure;
using UserManagement.Security;
namespace UserManagement.Application;
// Expose only the approved page envelope, without total counts or protected fields.
public sealed record DirectoryPage(IReadOnlyList<UserView> Items, int Limit, int Skip);
// Require audit acknowledgement before returning any successful collection read.
public sealed class DirectoryService(UserRepository users, AuditRepository audit, IdentifierProtector protector)
{
    // Parse the original query and execute its safe projection without decrypting identifiers.
    // Args: actor is an authenticated administrator, never query-supplied identity.
    // Raises: Controlled validation or dependency errors; audit failure cannot return success.
    public async Task<DirectoryPage> ListAsync(IQueryCollection parameters, ObjectId actor, CancellationToken ct)
    {
        try
        {
            var query = UserQuery.Parse(parameters);
            var items = await users.ListDirectoryAsync(query, protector, ct);
            await audit.RecordAsync(AuditEvent.Create(actor, "user.list", true), ct);
            return new DirectoryPage(items, query.Limit, query.Skip);
        }
        catch
        {
            await audit.RecordAsync(AuditEvent.Create(actor, "user.list", false), ct);
            throw;
        }
    }
}
