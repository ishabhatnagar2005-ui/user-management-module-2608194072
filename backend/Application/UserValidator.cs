// Validate the create allowlist before hashing or writing account state.
using System.Text.Json;
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
// Preserve absent-versus-null patch semantics without admitting lifecycle or credential members.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateUserRequest(JsonElement Username = default, JsonElement Address = default,
    JsonElement City = default, JsonElement Country = default, JsonElement Education = default,
    [property: JsonPropertyName("aadhaar_no")] JsonElement AadhaarNo = default,
    [property: JsonPropertyName("pan_no")] JsonElement PanNo = default);
// Centralize bounded validation without normalizing exact-case usernames.
public sealed class UserValidator
{
    // Reject unsupported usernames and oversized optional text without echoing values.
    // Raises: ApiException when any supplied field violates the create contract.
    public void Validate(CreateUserRequest request)
    {
        var errors = ValidateFields(request);
        try { PasswordHasher.Validate(request.Password); }
        catch (ApiException error) { foreach (var field in error.Errors) errors[field.Key] = field.Value; }
        if (errors.Count > 0) throw ApiException.Invalid(errors);
    }
    // Collect shared public-field errors without validating or synthesizing credentials.
    private static Dictionary<string, string[]> ValidateFields(CreateUserRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Username is null || !Regex.IsMatch(request.Username, @"\A[A-Za-z0-9_.-]{3,64}\z"))
            errors["username"] = ["Use 3–64 letters, digits, dots, hyphens or underscores."];
        foreach (var (field, value, maximum) in new[] { ("address", request.Address, 500), ("city", request.City, 100),
            ("country", request.Country, 100), ("education", request.Education, 200), ("aadhaar_no", request.AadhaarNo, 64), ("pan_no", request.PanNo, 64) })
            if (value is not null && (value.Length > maximum || value.Any(char.IsControl))) errors[field] = [$"Use at most {maximum} characters without control characters."];
        return errors;
    }
    // Merge the allowlist and validate the complete effective public/identifier record before writing.
    // Returns: Validated field values; absent fields remain unchanged, optional null clears a field.
    public Dictionary<string, string?> Effective(UpdateUserRequest patch, UserManagement.Domain.UserRecord current,
        IdentifierProtector protector)
    {
        var values = new Dictionary<string, string?> { ["username"] = current.Username, ["address"] = current.Address,
            ["city"] = current.City, ["country"] = current.Country, ["education"] = current.Education,
            ["aadhaar_no"] = current.AadhaarNo is null ? null : protector.Unprotect("aadhaar_no", current.AadhaarNo),
            ["pan_no"] = current.PanNo is null ? null : protector.Unprotect("pan_no", current.PanNo) };
        foreach (var (field, value) in new[] { ("username", patch.Username), ("address", patch.Address), ("city", patch.City),
            ("country", patch.Country), ("education", patch.Education), ("aadhaar_no", patch.AadhaarNo), ("pan_no", patch.PanNo) })
        {
            if (value.ValueKind == JsonValueKind.Undefined) continue;
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw ApiException.Invalid(new() { [field] = ["Supply text or an optional null value."] });
            values[field] = value.ValueKind == JsonValueKind.Null ? null : value.GetString();
        }
        var errors = ValidateFields(new CreateUserRequest(values["username"], null, values["address"], values["city"],
            values["country"], values["education"], values["aadhaar_no"], values["pan_no"]));
        if (errors.Count > 0) throw ApiException.Invalid(errors);
        return values;
    }
    // Coerce a route identity before issuing a typed database filter.
    // Returns: Parsed identity. Raises: ApiException for malformed route identifiers.
    public ObjectId ParseId(string id) => ObjectId.TryParse(id, out var parsed) ? parsed :
        throw ApiException.Invalid(new() { ["id"] = ["Supply a valid account identifier."] });
}
