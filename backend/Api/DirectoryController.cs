// Expose the administrator-only directory collection and safe pre-handler denial audits.
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UserManagement.Application;
using UserManagement.Audit;
using UserManagement.Domain;
namespace UserManagement.Api;
// Keep query validation and database operations in the application layer.
[ApiController, Route("api/users"), Authorize(Policy = "Admin")]
public sealed class DirectoryController(DirectoryService directory) : ControllerBase
{
    // Preserve duplicate and unknown URL parameters for strict application validation.
    [HttpGet]
    public async Task<ActionResult<DirectoryPage>> List(CancellationToken ct)
    {
        HttpContext.Items[DirectoryFailureAuditMiddleware.Handled] = true;
        return Ok(await directory.ListAsync(Request.Query, ObjectId.Parse(User.FindFirst("sub")!.Value), ct));
    }
}
// Audit denied collection reads without changing existing account middleware behavior.
public sealed class DirectoryFailureAuditMiddleware(RequestDelegate next)
{
    public const string Handled = "directory-service-audit";
    // Buffer the bounded response so an unavailable audit can still produce a safe 503.
    // Notes: Unknown actors are omitted and no query values enter the audit.
    public async Task InvokeAsync(HttpContext context, AuditRepository audit)
    {
        if (context.Request.Method != "GET" || !string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/api/users", StringComparison.OrdinalIgnoreCase)) { await next(context); return; }
        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            try { await next(context); }
            catch { if (!context.Items.ContainsKey(Handled)) await FailureAsync(context, audit); throw; }
            if (context.Response.StatusCode >= 400 && !context.Items.ContainsKey(Handled)) await FailureAsync(context, audit);
            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted);
        }
        finally { context.Response.Body = original; }
    }
    // Persist only verified actor identity and the fixed list-failure vocabulary.
    private static Task FailureAsync(HttpContext context, AuditRepository audit)
    {
        ObjectId? actor = context.User.Identity?.IsAuthenticated == true && ObjectId.TryParse(context.User.FindFirst("sub")?.Value, out var id) ? id : null;
        return audit.RecordAsync(AuditEvent.Create(actor, "user.list", false), context.RequestAborted);
    }
}
