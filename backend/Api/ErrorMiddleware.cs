// Redact failures, unify HTTP errors, and prevent browser/shared caching of API responses.
using MongoDB.Driver;
namespace UserManagement.Api;
public sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> logger)
{
    // Attach server-generated correlation and convert dependency errors without secret echo.
    public async Task InvokeAsync(HttpContext context)
    {
        context.TraceIdentifier = Guid.NewGuid().ToString("N");
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        try
        {
            await next(context);
            if (context.Response.StatusCode >= 400 && !context.Response.HasStarted && context.Response.ContentLength is null)
                await WriteAsync(context, context.Response.StatusCode, context.Response.StatusCode == 401 ? "authentication_required" : context.Response.StatusCode == 403 ? "access_denied" : "request_failed", context.Response.StatusCode == 401 ? "Sign in is required" : context.Response.StatusCode == 403 ? "Access is not permitted" : "Request could not be completed", []);
        }
        catch (ApiException error) { await WriteAsync(context, error.Status, error.Type, error.Message, error.Errors); }
        catch (Exception error) when (error is MongoException or TimeoutException)
        {
            logger.LogWarning("Dependency unavailable for request {CorrelationId}", context.TraceIdentifier);
            await WriteAsync(context, 503, "dependency_unavailable", "Service is temporarily unavailable", []);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Response.StatusCode = 499; }
        catch (Exception)
        {
            logger.LogError("Unhandled request failure {CorrelationId}", context.TraceIdentifier);
            await WriteAsync(context, 500, "internal_error", "Request could not be completed", []);
        }
    }
    // Write a safe structured envelope; callers must not supply raw framework exception text.
    public static async Task WriteAsync(HttpContext context, int status, string type, string title, Dictionary<string, string[]> errors)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ApiError(type, title, status, errors, context.TraceIdentifier));
    }
}
