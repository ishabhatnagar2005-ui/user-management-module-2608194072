// Build six summaries from a single Mongo snapshot, never from cached or fabricated values.
using UserManagement.Api;
using UserManagement.Audit;
using UserManagement.Infrastructure;
namespace UserManagement.Application;
// Count only populated source countries, retaining exact stored labels.
public sealed record CountryCount(string Country, long Count);
// Summarize recorded outcomes for one approved action, not invented activity categories.
public sealed record AuditActivity(string Action, long Success, long Failure);
// Return the six approved summaries without personal account fields or a time-window claim.
public sealed record DashboardView(long Total, long Active, long Inactive, IReadOnlyList<AuditView> RecentLogins,
    IReadOnlyList<CountryCount> CountryDistribution, IReadOnlyList<AuditActivity> AuditSummary);
// Coordinate cross-collection snapshot reads and fail closed on inconsistent source status.
public sealed class DashboardService(MongoStore store, UserRepository users, AuditRepository audit)
{
    // Reject query controls and read counts, latest ten login outcomes and full approved audit groups.
    // Raises: A safe 503 for inconsistent source counts; Mongo dependency failures propagate to middleware.
    public Task<DashboardView> ReadAsync(IQueryCollection query, CancellationToken ct)
    {
        AuditService.RejectQuery(query);
        return store.TransactionAsync(async (session, token) =>
        {
            var source = await users.ActivityCountsAsync(session, token);
            var counts = source["counts"].AsBsonArray;
            var total = counts.Count == 0 ? 0 : counts[0]["total"].ToInt64();
            var active = counts.Count == 0 ? 0 : counts[0]["active"].ToInt64();
            var inactive = counts.Count == 0 ? 0 : counts[0]["inactive"].ToInt64();
            if (total != active + inactive) throw new ApiException(503, "dependency_unavailable", "Service is temporarily unavailable");
            var countries = source["countries"].AsBsonArray.Select(x => new CountryCount(x["_id"].AsString, x["count"].ToInt64())).ToList();
            var logins = await audit.ReadSafeAsync(10, true, session, token);
            var summary = await audit.SummarizeAsync(session, token);
            return new DashboardView(total, active, inactive, logins, countries, summary);
        }, ct);
    }
}
