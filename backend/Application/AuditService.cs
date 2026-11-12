// Expose only approved, redacted audit outcomes; activity reads never generate new categories.
using System.Text.Json.Serialization;
using MongoDB.Bson;
using UserManagement.Api;
using UserManagement.Audit;
namespace UserManagement.Application;
// Omit an unknown actor rather than inventing identity or exposing personal content.
public sealed record AuditView(string Id,
    [property: JsonPropertyName("user_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserId,
    string Action, string Status, DateTime Timestamp);
// Keep the approved collection envelope free of paging or export contracts.
public sealed record AuditPage(IReadOnlyList<AuditView> Items);
// Read a fixed bounded recent collection through a server-side projection.
public sealed class AuditService(AuditRepository audit)
{
    // Reject all query controls and return the latest 100 approved records, newest first.
    // Raises: Validation and dependency failures; no cached results are retained.
    public async Task<AuditPage> ReadAsync(IQueryCollection query, CancellationToken ct)
    {
        RejectQuery(query);
        return new(await audit.ReadSafeAsync(100, false, null, ct));
    }
    // Keep unsupported search, sort, paging and export parameters out of activity reads.
    public static void RejectQuery(IQueryCollection query)
    {
        if (query.Count != 0) throw ApiException.Invalid(new() { ["query"] = ["Activity reads do not accept query parameters."] });
    }
    // Convert only projected safe BSON fields and omit actors that are not stored ObjectIds.
    public static AuditView Project(BsonDocument document) => new(document["_id"].AsObjectId.ToString(),
        document.TryGetValue("user_id", out var actor) && actor.IsObjectId ? actor.AsObjectId.ToString() : null,
        document["action"].AsString, document["status"].AsString, document["timestamp"].ToUniversalTime());
}
