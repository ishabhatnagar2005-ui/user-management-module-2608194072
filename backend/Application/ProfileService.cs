// Restrict own-account mutations to principal identity and atomic, redacted outcomes.
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api;
using UserManagement.Audit;
using UserManagement.Domain;
using UserManagement.Infrastructure;
using UserManagement.Security;
namespace UserManagement.Application;
// Preserve omitted fields and permit explicit null clearing without credential or identity members.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileUpdateRequest(JsonElement Address = default, JsonElement City = default,
    JsonElement Country = default, JsonElement Education = default);
// Accept secrets only for the explicit current-password-verified operation.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PasswordChangeRequest(string? CurrentPassword, string? NewPassword);
// Apply own-account policy independently of directory administrator privileges.
public sealed class ProfileService(MongoStore store, UserRepository users, AuditRepository audit, PasswordHasher hasher, TimeProvider clock)
{
    // Return a safe active account selected only by the verified principal.
    // Raises: Authentication failure when the account no longer exists or is inactive.
    public async Task<UserView> ReadAsync(ObjectId actor, CancellationToken ct) =>
        UserView.From(RequireActive(await users.FindByIdAsync(actor, ct)));
    // Update only four bounded optional fields, keeping absent fields unchanged.
    public Task<UserView> UpdateAsync(ObjectId actor, ProfileUpdateRequest patch, CancellationToken ct) =>
        MutateAsync(actor, "profile.update", current =>
        {
            var u = Builders<UserRecord>.Update;
            UpdateDefinition<UserRecord> update = u.Set(x => x.UpdatedAt, NextTimestamp(current));
            foreach (var (field, value, maximum) in new[] { ("address", patch.Address, 500), ("city", patch.City, 100),
                ("country", patch.Country, 100), ("education", patch.Education, 200) })
            {
                if (value.ValueKind == JsonValueKind.Undefined) continue;
                if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw ApiException.Invalid(new() { [field] = ["Supply text or an optional null value."] });
                var text = value.ValueKind == JsonValueKind.Null ? null : value.GetString();
                if (text is not null && (text.Length > maximum || text.Any(char.IsControl)))
                    throw ApiException.Invalid(new() { [field] = [$"Use at most {maximum} characters without control characters."] });
                update = text is null ? update.Unset(field) : update.Set(field, text);
            }
            return update;
        }, ct);
    // Verify current bcrypt before writing a bounded cost-12 replacement and invalidating all old tokens.
    // Raises: Safe validation failure for invalid secrets; no credential changes on any failure.
    public async Task ChangePasswordAsync(ObjectId actor, PasswordChangeRequest request, CancellationToken ct)
    {
        await MutateAsync(actor, "password.change", current =>
        {
            ValidatePassword(request.CurrentPassword, "currentPassword");
            ValidatePassword(request.NewPassword, "newPassword");
            if (!hasher.Verify(request.CurrentPassword!, current.PasswordHash))
                throw ApiException.Invalid(new() { ["currentPassword"] = ["Current password is incorrect."] });
            return Builders<UserRecord>.Update.Set(x => x.PasswordHash, hasher.Hash(request.NewPassword!))
                .Inc(x => x.CredentialVersion, 1).Set(x => x.UpdatedAt, NextTimestamp(current));
        }, ct);
    }
    // Enforce byte limits with field-specific errors, never echoing either secret.
    private static void ValidatePassword(string? password, string field)
    {
        try { PasswordHasher.Validate(password); }
        catch (ApiException) { throw ApiException.Invalid(new() { [field] = ["Use 12–72 UTF-8 bytes."] }); }
    }
    // Fail closed after account removal or deactivation, including races after JWT validation.
    private static UserRecord RequireActive(UserRecord? user) => user is { IsActive: true } ? user :
        throw new ApiException(401, "authentication_required", "Sign in is required");
    // Advance storage timestamps at UTC millisecond precision even under a fixed clock.
    private DateTime NextTimestamp(UserRecord user)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        return now > user.UpdatedAt ? now : user.UpdatedAt.AddMilliseconds(1);
    }
    // Commit the conditional active/version write and audit together; retry only aborted snapshots.
    // Notes: A retry re-verifies current credentials; no stale password can overwrite a newer credential.
    private async Task<UserView> MutateAsync(ObjectId actor, string action,
        Func<UserRecord, UpdateDefinition<UserRecord>> build, CancellationToken ct)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await store.TransactionAsync(async (session, token) =>
                    {
                        var current = RequireActive(await users.FindByIdAsync(actor, token, session));
                        var result = await users.UpdateCredentialConditionalAsync(current.Id, current.CredentialVersion,
                            build(current), session, token);
                        if (result is null) throw new ApiException(401, "authentication_required", "Sign in is required");
                        await audit.RecordAsync(AuditEvent.Create(actor, action, true), token, session);
                        return UserView.From(result);
                    }, ct);
                }
                catch (MongoException error) when (error.HasErrorLabel("TransientTransactionError") && attempt < 3)
                { await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), ct); }
            }
        }
        catch { await audit.RecordAsync(AuditEvent.Create(actor, action, false), ct); throw; }
    }
}
