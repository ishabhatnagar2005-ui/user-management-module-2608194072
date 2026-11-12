// Project safe account fields without credentials, identifiers, or operator policy.
using System.Text.Json.Serialization;
using UserManagement.Domain;
namespace UserManagement.Api;
public sealed record UserView(string Id, string Username, string? Address, string? City, string? Country, string? Education,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt)
{
    // Convert BSON identity at the boundary and explicitly allowlist visible values.
    public static UserView From(UserRecord user) => new(user.Id.ToString(), user.Username, user.Address, user.City, user.Country, user.Education, user.IsActive, user.CreatedAt, user.UpdatedAt);
}
