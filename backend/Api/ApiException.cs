// Carry controlled errors without retaining submitted values or dependency details.
namespace UserManagement.Api;
public sealed class ApiException(int status, string type, string title, Dictionary<string, string[]>? errors = null) : Exception(title)
{
    public int Status { get; } = status;
    public string Type { get; } = type;
    public Dictionary<string, string[]> Errors { get; } = errors ?? [];
    // Reject an invalid request using safe field guidance.
    public static ApiException Invalid(Dictionary<string, string[]> errors) => new(400, "validation_error", "Request validation failed", errors);
}
public sealed record ApiError(string Type, string Title, int Status, Dictionary<string, string[]> Errors, string CorrelationId);
