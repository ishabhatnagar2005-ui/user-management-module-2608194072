// Expose principal-only profile operations to both authenticated account roles.
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UserManagement.Application;
namespace UserManagement.Api;
// Delegate own-account policy to the application layer without accepting a target identity.
[ApiController, Route("api/profile"), Authorize]
public sealed class ProfileController(ProfileService profile) : ControllerBase
{
    // Read a safe projection without mutating profile or credentials.
    [HttpGet]
    public async Task<ActionResult<UserView>> Read(CancellationToken ct) =>
        Ok(await profile.ReadAsync(ObjectId.Parse(User.FindFirst("sub")!.Value), ct));
    // Apply the four-field allowlist and mark service-owned mutation auditing.
    [HttpPut]
    public async Task<ActionResult<UserView>> Update([FromBody] ProfileUpdateRequest request, CancellationToken ct)
    {
        HttpContext.Items[AccountFailureAuditMiddleware.Handled] = true;
        return Ok(await profile.UpdateAsync(ObjectId.Parse(User.FindFirst("sub")!.Value), request, ct));
    }
    // Replace credentials only after current-password proof; do not return secrets or a new token.
    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword([FromBody] PasswordChangeRequest request, CancellationToken ct)
    {
        HttpContext.Items[AccountFailureAuditMiddleware.Handled] = true;
        await profile.ChangePasswordAsync(ObjectId.Parse(User.FindFirst("sub")!.Value), request, ct);
        return NoContent();
    }
}
