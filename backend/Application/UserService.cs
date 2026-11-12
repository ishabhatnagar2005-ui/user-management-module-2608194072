// Coordinate safe account creation and reads with redacted durable outcomes.
using MongoDB.Bson;
using MongoDB.Driver;
using UserManagement.Api;
using UserManagement.Audit;
using UserManagement.Domain;
using UserManagement.Infrastructure;
using UserManagement.Security;
namespace UserManagement.Application;
// Execute only the approved create/read account use cases.
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
}
