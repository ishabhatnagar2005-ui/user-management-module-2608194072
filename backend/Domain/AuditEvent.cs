// Persist only the approved audit fields, omitting an unavailable actor.
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
namespace UserManagement.Domain;
public sealed class AuditEvent
{
    [BsonId] public ObjectId Id { get; set; } = ObjectId.GenerateNewId();
    [BsonElement("user_id"), BsonIgnoreIfNull] public ObjectId? UserId { get; set; }
    [BsonElement("action")] public string Action { get; set; } = "";
    [BsonElement("status")] public string Status { get; set; } = "";
    [BsonElement("timestamp"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    // Construct a redacted outcome; reject unsupported vocabulary before persistence.
    public static AuditEvent Create(ObjectId? actor, string action, bool success) =>
        new() { UserId = actor, Action = AllowedActions.Contains(action) ? action : throw new ArgumentException("Unsupported audit action"), Status = success ? "success" : "failure" };
    private static readonly HashSet<string> AllowedActions = ["login", "user.create", "user.read", "user.list", "user.update", "user.deactivate", "profile.update", "password.change"];
}
