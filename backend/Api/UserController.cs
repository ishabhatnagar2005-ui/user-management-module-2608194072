// Expose administrator-only create/read endpoints and audit pre-handler denials.
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UserManagement.Application;
using UserManagement.Audit;
using UserManagement.Domain;
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
}
// Record account failures that happen before MVC reaches the service, including JSON and role denial.
public sealed class AccountFailureAuditMiddleware(RequestDelegate next)
{
    public const string Handled = "account-service-audit";
    // Wrap only create/read paths; preserve authentication behavior on every other route.
    // Notes: Unknown actors are omitted; a unavailable audit never becomes a successful response.
    public async Task InvokeAsync(HttpContext context, AuditRepository audit)
    {
        var path = context.Request.Path.Value ?? "";
        var action = context.Request.Method == "POST" && path.TrimEnd('/') == "/api/users" ? "user.create" :
            context.Request.Method == "GET" && path.StartsWith("/api/users/", StringComparison.Ordinal) && path[11..].TrimEnd('/').IndexOf('/') < 0 ? "user.read" : null;
        if (action is null) { await next(context); return; }
        // Buffer this small account response so audit failure can still produce a controlled 503.
        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            try { await next(context); }
            catch
            {
                if (!context.Items.ContainsKey(Handled)) await RecordFailureAsync(context, audit, action);
                throw;
            }
            if (!context.Items.ContainsKey(Handled) && context.Response.StatusCode >= 400)
                await RecordFailureAsync(context, audit, action);
            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted);
        }
        finally { context.Response.Body = original; }
    }
    // Omit unverified identity and persist only the approved failure vocabulary.
    private static Task RecordFailureAsync(HttpContext context, AuditRepository audit, string action)
    {
        ObjectId? actor = context.User.Identity?.IsAuthenticated == true && ObjectId.TryParse(context.User.FindFirst("sub")?.Value, out var id) ? id : null;
        return audit.RecordAsync(AuditEvent.Create(actor, action, false), context.RequestAborted);
    }
}
