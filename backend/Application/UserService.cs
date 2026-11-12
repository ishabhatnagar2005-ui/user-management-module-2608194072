// Coordinate safe account management with redacted durable outcomes.
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api;
using UserManagement.Audit;
using UserManagement.Domain;
using UserManagement.Infrastructure;
using UserManagement.Security;
namespace UserManagement.Application;
// Execute approved account use cases with transactional mutation auditing.
public sealed class UserService(MongoStore store, UserRepository users, AuditRepository audit, UserValidator validator,
    PasswordHasher hasher, IdentifierProtector protector, TimeProvider clock)
{
    // Persist a default-User account and its success event in one majority transaction.
    // Args: actor is the authenticated administrator, never a submitted identity.
    // Raises: Controlled validation/conflict errors or dependency failure; no partial user write.
    public async Task<UserView> CreateAsync(CreateUserRequest request, ObjectId actor, CancellationToken ct)
    {
        try
        {
            validator.Validate(request);
            var now = clock.GetUtcNow().UtcDateTime;
            now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
            var user = new UserRecord { Username = request.Username!, PasswordHash = hasher.Hash(request.Password!),
                Address = request.Address, City = request.City, Country = request.Country, Education = request.Education,
                Role = UserRole.User, CredentialVersion = 1, IsActive = true, CreatedAt = now, UpdatedAt = now,
                AadhaarNo = request.AadhaarNo is { Length: > 0 } ? protector.Protect("aadhaar_no", request.AadhaarNo) : null,
                AadhaarIndex = request.AadhaarNo is { Length: > 0 } ? protector.BlindIndex("aadhaar_no", request.AadhaarNo) : null,
                PanNo = request.PanNo is { Length: > 0 } ? protector.Protect("pan_no", request.PanNo) : null,
                PanIndex = request.PanNo is { Length: > 0 } ? protector.BlindIndex("pan_no", request.PanNo) : null };
            return await store.TransactionAsync(async (session, token) =>
            {
                await users.InsertAsync(user, token, session);
                await audit.RecordAsync(AuditEvent.Create(actor, "user.create", true), token, session);
                return UserView.From(user);
            }, ct);
        }
        catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            await audit.RecordAsync(AuditEvent.Create(actor, "user.create", false), ct);
            throw new ApiException(409, "username_conflict", "Username is already in use", new() { ["username"] = ["Choose another username."] });
        }
        catch
        {
            await audit.RecordAsync(AuditEvent.Create(actor, "user.create", false), ct);
            throw;
        }
    }
    // Read only safe fields and durably record success or missing/malformed failure.
    // Raises: ApiException for malformed or absent records; dependency errors propagate safely.
    public async Task<UserView> ReadAsync(string id, ObjectId actor, CancellationToken ct)
    {
        try
        {
            var user = await users.FindByIdAsync(validator.ParseId(id), ct);
            if (user is null) throw new ApiException(404, "account_not_found", "Account was not found");
            await audit.RecordAsync(AuditEvent.Create(actor, "user.read", true), ct);
            return UserView.From(user);
        }
        catch
        {
            await audit.RecordAsync(AuditEvent.Create(actor, "user.read", false), ct);
            throw;
        }
    }
    // Update a validated effective record with one write, preserving all untouched storage fields.
    public Task<UserView> UpdateAsync(string id, UpdateUserRequest patch, ObjectId actor, CancellationToken ct) =>
        MutateAsync(id, patch, actor, "user.update", ct);
    // Soft-deactivate without removing data or providing a reactivation path.
    public Task<UserView> DeactivateAsync(string id, ObjectId actor, CancellationToken ct) =>
        MutateAsync(id, null, actor, "user.deactivate", ct);
    // Read/validate/write/audit within the same snapshot; retry only aborted transient transactions.
    // Raises: Safe missing/validation/conflict errors; unavailable dependencies never use memory fallback.
    private async Task<UserView> MutateAsync(string id, UpdateUserRequest? patch, ObjectId actor, string action, CancellationToken ct)
    {
        try
        {
            var parsed = validator.ParseId(id);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await store.TransactionAsync(async (session, token) =>
                    {
                        var current = await users.FindByIdAsync(parsed, token, session);
                        if (current is null) throw new ApiException(404, "account_not_found", "Account was not found");
                        var now = clock.GetUtcNow().UtcDateTime;
                        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
                        if (now <= current.UpdatedAt) now = current.UpdatedAt.AddMilliseconds(1);
                        var u = Builders<UserRecord>.Update;
                        UpdateDefinition<UserRecord> update = u.Set(x => x.UpdatedAt, now);
                        if (patch is null) update = update.Set(x => x.IsActive, false);
                        else
                        {
                            var values = validator.Effective(patch, current, protector);
                            foreach (var (field, value) in values)
                            {
                                if (field is "aadhaar_no" or "pan_no")
                                {
                                    var supplied = field == "aadhaar_no" ? patch.AadhaarNo : patch.PanNo;
                                    if (supplied.ValueKind == System.Text.Json.JsonValueKind.Undefined) continue;
                                    var index = field == "aadhaar_no" ? "aadhaar_index" : "pan_index";
                                    update = string.IsNullOrEmpty(value) ? update.Unset(field).Unset(index) :
                                        update.Set(field, protector.Protect(field, value)).Set(index, protector.BlindIndex(field, value));
                                }
                                else
                                {
                                    var supplied = field switch { "username" => patch.Username, "address" => patch.Address,
                                        "city" => patch.City, "country" => patch.Country, _ => patch.Education };
                                    if (supplied.ValueKind != System.Text.Json.JsonValueKind.Undefined)
                                        update = value is null ? update.Unset(field) : update.Set(field, value);
                                }
                            }
                        }
                        var result = await users.UpdateAsync(Builders<UserRecord>.Filter.Eq(x => x.Id, parsed), update, session, token);
                        await audit.RecordAsync(AuditEvent.Create(actor, action, true), token, session);
                        return UserView.From(result);
                    }, ct);
                }
                catch (MongoException error) when (error.HasErrorLabel("TransientTransactionError") && attempt < 3)
                {
                    // The transaction was aborted; a fresh snapshot must be merged before retry.
                    await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), ct);
                }
            }
        }
        catch (Exception error) when (error is MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey } or MongoCommandException { Code: 11000 })
        {
            await audit.RecordAsync(AuditEvent.Create(actor, action, false), ct);
            throw new ApiException(409, "username_conflict", "Username is already in use", new() { ["username"] = ["Choose another username."] });
        }
        catch
        {
            await audit.RecordAsync(AuditEvent.Create(actor, action, false), ct);
            throw;
        }
    }
}
