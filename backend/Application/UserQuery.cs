// Parse an immutable allowlisted directory query before constructing Mongo operators.
using System.Globalization;
using UserManagement.Api;
namespace UserManagement.Application;
// Keep validated query values separate from untrusted URL parameters.
public sealed record UserQuery
{
    public int Limit { get; private init; } = 25;
    public int Skip { get; private init; }
    public string? Search { get; private init; }
    public string? Country { get; private init; }
    public string? Education { get; private init; }
    public bool? Active { get; private init; }
    public string SortBy { get; private init; } = "username";
    public bool Descending { get; private init; }
    // Prevent unvalidated construction outside the parser.
    private UserQuery() { }
    // Validate exact parameter names, cardinality and bounded literal values.
    // Args: parameters are the original query collection, not MVC's lossy scalar binding.
    // Returns: An immutable query with approved defaults.
    // Raises: Safe 400 errors without retaining submitted names or values.
    public static UserQuery Parse(IQueryCollection parameters)
    {
        string[] allowed = ["limit", "skip", "search", "country", "education", "status", "sortBy", "sortDirection"];
        if (parameters.Count > allowed.Length || parameters.Any(p => !allowed.Contains(p.Key, StringComparer.Ordinal) || p.Value.Count != 1) ||
            parameters.Sum(p => p.Key.Length + p.Value.ToString().Length) > 4096) throw Invalid();
        var limit = Number(parameters, "limit", 25);
        var skip = Number(parameters, "skip", 0);
        if (limit is < 1 or > 100 || skip < 0) throw Invalid();
        var search = Literal(parameters, "search", 200);
        var country = Literal(parameters, "country", 100);
        var education = Literal(parameters, "education", 200);
        var status = Literal(parameters, "status", 8);
        var sort = Literal(parameters, "sortBy", 16) ?? "username";
        var direction = Literal(parameters, "sortDirection", 4) ?? "asc";
        if (status is not (null or "active" or "inactive") || sort is not ("username" or "city" or "country" or "education") || direction is not ("asc" or "desc")) throw Invalid();
        return new UserQuery { Limit = limit, Skip = skip, Search = search, Country = country, Education = education,
            Active = status is null ? null : status == "active", SortBy = sort, Descending = direction == "desc" };
    }
    // Reject empty, signed, fractional and overflow numeric inputs rather than silently defaulting.
    private static int Number(IQueryCollection parameters, string key, int fallback)
    {
        if (!parameters.TryGetValue(key, out var values)) return fallback;
        var value = values[0];
        if (string.IsNullOrEmpty(value) || !value.All(char.IsAsciiDigit) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)) throw Invalid();
        return result;
    }
    // Accept bounded literal text, including regex punctuation, but not operator-shaped inputs.
    private static string? Literal(IQueryCollection parameters, string key, int maximum)
    {
        if (!parameters.TryGetValue(key, out var values)) return null;
        var value = values[0] ?? "";
        if (value.Length > maximum || value.Any(c => char.IsControl(c) || c is '$' or '{' or '}' or '[' or ']')) throw Invalid();
        if (value.Length == 0) { if (key is "search" or "country" or "education") return null; throw Invalid(); }
        return value;
    }
    // Produce fixed validation guidance so hostile input never reaches the error envelope.
    private static ApiException Invalid() => ApiException.Invalid(new() { ["query"] = ["Use each supported parameter once with a bounded literal value and an approved sort or status."] });
}
