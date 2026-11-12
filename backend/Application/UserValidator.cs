// Validate the create allowlist before hashing or writing account state.
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using UserManagement.Api;
using UserManagement.Security;
namespace UserManagement.Application;
// Carry only client-writable creation fields; role and lifecycle fields are absent.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateUserRequest(string? Username, string? Password, string? Address = null, string? City = null,
    string? Country = null, string? Education = null,
    [property: JsonPropertyName("aadhaar_no")] string? AadhaarNo = null,
    [property: JsonPropertyName("pan_no")] string? PanNo = null);
// Centralize bounded validation without normalizing exact-case usernames.
public sealed class UserValidator
{
    // Reject unsupported usernames and oversized optional text without echoing values.
    // Raises: ApiException when any supplied field violates the create contract.
    public void Validate(CreateUserRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Username is null || !Regex.IsMatch(request.Username, @"\A[A-Za-z0-9_.-]{3,64}\z"))
            errors["username"] = ["Use 3–64 letters, digits, dots, hyphens or underscores."];
        foreach (var (field, value, maximum) in new[] { ("address", request.Address, 500), ("city", request.City, 100),
            ("country", request.Country, 100), ("education", request.Education, 200), ("aadhaar_no", request.AadhaarNo, 64), ("pan_no", request.PanNo, 64) })
            if (value is not null && (value.Length > maximum || value.Any(char.IsControl))) errors[field] = [$"Use at most {maximum} characters without control characters."];
        try { PasswordHasher.Validate(request.Password); }
        catch (ApiException error) { foreach (var field in error.Errors) errors[field.Key] = field.Value; }
        if (errors.Count > 0) throw ApiException.Invalid(errors);
    }
    // Coerce a route identity before issuing a typed database filter.
    // Returns: Parsed identity. Raises: ApiException for malformed route identifiers.
    public ObjectId ParseId(string id) => ObjectId.TryParse(id, out var parsed) ? parsed :
        throw ApiException.Invalid(new() { ["id"] = ["Supply a valid account identifier."] });
}
