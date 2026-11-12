// Verify cross-feature identity, persistence, audits and failure propagation through real HTTP.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Driver;
using UserManagement.Domain;
using UserManagement.Infrastructure;
using Xunit;
namespace UserManagement.Tests;
public sealed class IntegrationTests
{
    // Obtain tokens through the real login contract rather than bypassing authentication.
    private static async Task<string> Login(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString()!;
    }
    // Create, authenticate, edit profile, change credentials, deactivate and reconcile dashboard/audit.
    [Fact]
    public async Task Full_account_lifecycle_reconciles_profile_directory_auth_dashboard_and_logs()
    {
        await using var app = new TestApplication();
        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", await Login(admin, "test.admin", app.Password));
        var created = await admin.PostAsJsonAsync("/api/users", new { username = "chain.user", password = app.Password, country = "Synthetic Country" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        using var user = app.CreateClient();
        var originalToken = await Login(user, "chain.user", app.Password);
        user.DefaultRequestHeaders.Authorization = new("Bearer", originalToken);
        var profile = await user.PutAsJsonAsync("/api/profile", new { city = "Cross-feature city" });
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        var detail = await admin.GetAsync("/api/users/" + id);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal("Cross-feature city", JsonDocument.Parse(await detail.Content.ReadAsStringAsync()).RootElement.GetProperty("city").GetString());
        var list = await admin.GetAsync("/api/users?search=chain.user&country=Synthetic%20Country&limit=25&skip=0");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Single(JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());
        var newPassword = app.Password + "x";
        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsJsonAsync("/api/profile/password", new { currentPassword = app.Password, newPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/profile")).StatusCode);
        user.DefaultRequestHeaders.Authorization = new("Bearer", await Login(user, "chain.user", newPassword));
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/users/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/profile")).StatusCode);
        var saved = await app.Service<UserRepository>().FindByUsernameAsync("chain.user", default);
        Assert.NotNull(saved); Assert.False(saved.IsActive); Assert.Equal(2, saved.CredentialVersion);
        Assert.Equal("Cross-feature city", saved.City);
        var dashboard = await admin.GetAsync("/api/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        var counts = JsonDocument.Parse(await dashboard.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(3, counts.GetProperty("total").GetInt32());
        Assert.Equal(2, counts.GetProperty("active").GetInt32());
        Assert.Equal(1, counts.GetProperty("inactive").GetInt32());
        var logs = await admin.GetAsync("/api/logs");
        Assert.Equal(HttpStatusCode.OK, logs.StatusCode);
        var actions = JsonDocument.Parse(await logs.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("action").GetString()).ToList();
        foreach (var action in new[] { "login", "user.create", "user.read", "user.list", "profile.update", "password.change", "user.deactivate" }) Assert.Contains(action, actions);
        var rawLogs = await app.Service<MongoStore>().Logs.Find(FilterDefinition<AuditEvent>.Empty).ToListAsync();
        var administrator = await app.Service<UserRepository>().FindByUsernameAsync("test.admin", default);
        Assert.NotNull(administrator);
        Assert.DoesNotContain(rawLogs, x => x.UserId is not null && x.UserId != saved.Id && x.UserId != administrator.Id);
    }
    // Failed writes must not leak across directory, profile or dashboard boundaries.
    [Fact]
    public async Task Invalid_cross_account_and_duplicate_operations_preserve_shared_state()
    {
        await using var app = new TestApplication(); using var admin = app.CreateClient(); using var user = app.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", await Login(admin, "test.admin", app.Password));
        user.DefaultRequestHeaders.Authorization = new("Bearer", await Login(user, "test.user", app.Password));
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PutAsJsonAsync("/api/profile", new { id = "000000000000000000000001", city = "forbidden" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/users", new { username = "test.user", password = app.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsJsonAsync("/api/profile/password", new { currentPassword = "incorrect-password", newPassword = app.Password + "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/profile")).StatusCode);
        var store = app.Service<MongoStore>();
        Assert.Equal(2, await store.Users.CountDocumentsAsync(FilterDefinition<UserRecord>.Empty));
        var saved = await app.Service<UserRepository>().FindByUsernameAsync("test.user", default);
        Assert.NotNull(saved); Assert.NotEqual("forbidden", saved.City); Assert.Equal(1, saved.CredentialVersion);
        var dashboard = await admin.GetAsync("/api/dashboard"); Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Equal(2, JsonDocument.Parse(await dashboard.Content.ReadAsStringAsync()).RootElement.GetProperty("total").GetInt32());
        Assert.True(await store.Logs.CountDocumentsAsync(x => x.Status == "failure") >= 3);
    }
}
