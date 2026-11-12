// Persist account state; never expose this password-bearing document over HTTP.
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
namespace UserManagement.Domain;
public enum UserRole { User, Admin }
public sealed class UserRecord
{
    [BsonId] public ObjectId Id { get; set; } = ObjectId.GenerateNewId();
    [BsonElement("username")] public string Username { get; set; } = "";
    [BsonElement("password")] public string PasswordHash { get; set; } = "";
    [BsonElement("address"), BsonIgnoreIfNull] public string? Address { get; set; }
    [BsonElement("city"), BsonIgnoreIfNull] public string? City { get; set; }
    [BsonElement("country"), BsonIgnoreIfNull] public string? Country { get; set; }
    [BsonElement("education"), BsonIgnoreIfNull] public string? Education { get; set; }
    [BsonElement("aadhaar_no"), BsonIgnoreIfNull] public string? AadhaarNo { get; set; }
    [BsonElement("pan_no"), BsonIgnoreIfNull] public string? PanNo { get; set; }
    [BsonElement("aadhaar_index"), BsonIgnoreIfNull] public string? AadhaarIndex { get; set; }
    [BsonElement("pan_index"), BsonIgnoreIfNull] public string? PanIndex { get; set; }
    [BsonElement("is_active")] public bool IsActive { get; set; } = true;
    [BsonElement("role"), BsonRepresentation(BsonType.String)] public UserRole Role { get; set; } = UserRole.User;
    [BsonElement("credential_version")] public long CredentialVersion { get; set; } = 1;
    [BsonElement("created_at"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [BsonElement("updated_at"), BsonDateTimeOptions(Kind = DateTimeKind.Utc)] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
