// Audit safe pre-handler account failures independently of endpoint responsibilities.
using MongoDB.Bson;
using UserManagement.Audit;
using UserManagement.Domain;
namespace UserManagement.Api;
// Record account failures that happen before MVC reaches the service, including JSON and role denial.
public sealed class AccountFailureAuditMiddleware(RequestDelegate next)
{
    public const string Handled = "account-service-audit";
    // Wrap only approved account paths; preserve authentication behavior on every other route.
    // Notes: Unknown actors are omitted; a unavailable audit never becomes a successful response.
    public async Task InvokeAsync(HttpContext context, AuditRepository audit)
    {
        var path = context.Request.Path.Value ?? "";
        var action = context.Request.Method == "POST" && path.TrimEnd('/') == "/api/users" ? "user.create" :
            context.Request.Method is "GET" or "PUT" or "DELETE" && path.StartsWith("/api/users/", StringComparison.Ordinal) && path[11..].TrimEnd('/').IndexOf('/') < 0 ? context.Request.Method switch { "PUT" => "user.update", "DELETE" => "user.deactivate", _ => "user.read" } : null;
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
