// Expose administrator-only create/read endpoints and audit pre-handler denials.
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UserManagement.Application;
namespace UserManagement.Api;
// Delegate validation and transactional persistence to the application layer.
[ApiController, Route("api/users"), Authorize(Policy = "Admin")]
public sealed class UserController(UserService users) : ControllerBase
{
    // Create only on explicit POST and publish the canonical safe detail location.
    [HttpPost]
    public async Task<ActionResult<UserView>> Create([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        HttpContext.Items[AccountFailureAuditMiddleware.Handled] = true;
        var user = await users.CreateAsync(request, ObjectId.Parse(User.FindFirst("sub")!.Value), ct);
        return CreatedAtAction(nameof(Read), new { id = user.Id }, user);
    }
    // Read a safe projection without changing account state.
    [HttpGet("{id}")]
    public async Task<ActionResult<UserView>> Read(string id, CancellationToken ct)
    {
        HttpContext.Items[AccountFailureAuditMiddleware.Handled] = true;
        return Ok(await users.ReadAsync(id, ObjectId.Parse(User.FindFirst("sub")!.Value), ct));
    }
    // Apply only the approved patch in an audited transaction.
    [HttpPut("{id}")]
    public async Task<ActionResult<UserView>> Update(string id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        HttpContext.Items[AccountFailureAuditMiddleware.Handled] = true;
        return Ok(await users.UpdateAsync(id, request, ObjectId.Parse(User.FindFirst("sub")!.Value), ct));
    }
    // Retain the account document and deactivate only on explicit DELETE.
    [HttpDelete("{id}")]
    public async Task<ActionResult<UserView>> Deactivate(string id, CancellationToken ct)
    {
        HttpContext.Items[AccountFailureAuditMiddleware.Handled] = true;
        return Ok(await users.DeactivateAsync(id, ObjectId.Parse(User.FindFirst("sub")!.Value), ct));
    }
}
