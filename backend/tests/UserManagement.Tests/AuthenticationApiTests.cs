// Verify real HTTP token, validation, role, audit and dependency boundaries.
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using UserManagement.Application;
using UserManagement.Domain;
using UserManagement.Infrastructure;
using UserManagement.Security;
using Xunit;
namespace UserManagement.Tests;
// Supply gate probes solely through the test assembly, not through production business routes.
[ApiController, Route("__test/gate")]
public sealed class AuthenticationGateController : ControllerBase
{
    [Authorize, HttpGet] public IActionResult Gate() => Ok(new { identity = User.FindFirst("sub")!.Value });
    [Authorize(Policy = "Admin"), HttpGet("admin")] public IActionResult Admin() => Ok(new { access = "admin" });
}
public sealed class AuthenticationApiTests
{
    [Fact]
    public async Task Login_returns_safe_contract_and_reaches_admin_gate_with_durable_log()
    {
        await using var app = new TestApplication(); using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/login", new LoginRequest("test.admin", app.Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(new[] { "accessToken", "expiresAt", "tokenType" }, json.EnumerateObject().Select(x => x.Name).Order().ToArray()); Assert.Equal("Bearer", json.GetProperty("tokenType").GetString());
        client.DefaultRequestHeaders.Authorization = new("Bearer", json.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/__test/gate/admin")).StatusCode);
        var user = await app.Service<UserRepository>().FindByUsernameAsync("test.admin", default);
        Assert.Equal(1, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.UserId == user!.Id && x.Action == "login" && x.Status == "success"));
    }
    [Fact]
    public async Task Bad_and_missing_accounts_have_identical_non_enumerating_shape()
    {
        await using var app = new TestApplication(); using var client = app.CreateClient();
        foreach (var username in new[] { "test.user", "unknown.user" })
        {
            var response = await client.PostAsJsonAsync("/api/login", new LoginRequest(username, "wrong-password"));
            await AssertError(response, HttpStatusCode.Unauthorized, "authentication_failed");
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement; Assert.Equal("Credentials could not be verified", body.GetProperty("title").GetString()); Assert.Empty(body.GetProperty("errors").EnumerateObject());
        }
        Assert.Equal(2, await app.Service<MongoStore>().Logs.CountDocumentsAsync(x => x.Status == "failure"));
    }
    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"username":"test.user"}""")]
    [InlineData("""{"password":"valid-password"}""")]
    [InlineData("""{"username":12,"password":"valid-password"}""")]
    [InlineData("""{"username":{"$ne":null},"password":"valid-password"}""")]
    [InlineData("""{"username":"../admin","password":"valid-password"}""")]
    [InlineData("""{"username":"<script>","password":"valid-password"}""")]
    [InlineData("""{"username":"test.user","password":"short"}""")]
    [InlineData("""{"username":"test.user","password":"valid-password","role":"Admin"}""")]
    [InlineData("not-json")]
    public async Task Invalid_and_unknown_fields_return_safe_400_without_audit(string body)
    {
        await using var app = new TestApplication(); using var client = app.CreateClient();
        var response = await client.PostAsync("/api/login", new StringContent(body, Encoding.UTF8, "application/json"));
        await AssertError(response, HttpStatusCode.BadRequest, "validation_error"); Assert.DoesNotContain("valid-password", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await app.Service<MongoStore>().Logs.CountDocumentsAsync(FilterDefinition<AuditEvent>.Empty));
    }
    [Theory]
    [InlineData(73, "a")]
    [InlineData(37, "é")]
    public async Task Password_utf8_overflow_is_rejected(int length, string character)
    {
        await using var app = new TestApplication(); using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/login", new LoginRequest("test.user", string.Concat(Enumerable.Repeat(character, length))));
        await AssertError(response, HttpStatusCode.BadRequest, "validation_error");
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expiry")]
    [InlineData("unsigned")]
    [InlineData("version")]
    [InlineData("identity")]
    [InlineData("role")]
    public async Task Untrusted_tokens_are_denied_before_protected_handler(string kind)
    {
        await using var app = new TestApplication(); using var client = app.CreateClient();
        if (kind != "missing") client.DefaultRequestHeaders.Authorization = new("Bearer", kind == "malformed" ? "not.a.token" : await Token(app, kind));
        await AssertError(await client.GetAsync("/__test/gate"), HttpStatusCode.Unauthorized, "authentication_required");
    }
    [Fact]
    public async Task Persisted_inactivity_and_credential_changes_reject_previously_valid_tokens()
    {
        await using var app = new TestApplication(); using var client = app.CreateClient();
        var token = await Token(app, "valid"); client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/__test/gate")).StatusCode);
        await app.Service<MongoStore>().Users.UpdateOneAsync(x => x.Username == "test.user", Builders<UserRecord>.Update.Inc(x => x.CredentialVersion, 1));
        await AssertError(await client.GetAsync("/__test/gate"), HttpStatusCode.Unauthorized, "authentication_required");
        client.DefaultRequestHeaders.Authorization = new("Bearer", await Token(app, "valid"));
        await app.Service<MongoStore>().Users.UpdateOneAsync(x => x.Username == "test.user", Builders<UserRecord>.Update.Set(x => x.IsActive, false));
        await AssertError(await client.GetAsync("/__test/gate"), HttpStatusCode.Unauthorized, "authentication_required");
        await AssertError(await client.PostAsJsonAsync("/api/login", new LoginRequest("test.user", app.Password)), HttpStatusCode.Unauthorized, "authentication_failed");
    }
    [Fact]
    public async Task User_is_authenticated_but_cannot_reach_admin_gate()
    {
        await using var app = new TestApplication(); using var client = app.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", await Token(app, "valid"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/__test/gate")).StatusCode);
        await AssertError(await client.GetAsync("/__test/gate/admin"), HttpStatusCode.Forbidden, "access_denied");
    }
    [Fact]
    public async Task Health_checks_real_dependency_and_returns_redacted_503_when_unreachable()
    {
        await using var app = new TestApplication(); using var client = app.CreateClient();
        var healthy = await client.GetAsync("/api/health"); Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.Equal("healthy", (await healthy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        app.DependencyUnavailable = true;
        var unavailable = await client.GetAsync("/api/health"); await AssertError(unavailable, HttpStatusCode.ServiceUnavailable, "dependency_unavailable");
        Assert.DoesNotContain("mongodb", await unavailable.Content.ReadAsStringAsync());
    }
    // Mint controlled invalid tokens for each gate branch; valid tokens still use real seeded state.
    private static async Task<string> Token(TestApplication app, string kind)
    {
        var user = (await app.Service<UserRepository>().FindByUsernameAsync("test.user", default))!;
        var secret = kind == "signature" ? Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)) : app.Settings["Jwt:Secret"]!;
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(kind == "issuer" ? "wrong-issuer" : app.Settings["Jwt:Issuer"], kind == "audience" ? "wrong-audience" : app.Settings["Jwt:Audience"],
            [new Claim("sub", kind == "identity" ? "invalid" : user.Id.ToString()), new Claim("role", kind == "role" ? "Admin" : "User"), new Claim("cv", kind == "version" ? "999" : user.CredentialVersion.ToString())],
            now.AddMinutes(-20), kind == "expiry" ? now.AddSeconds(-1) : now.AddMinutes(15),
            kind == "unsigned" ? null : new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
    // Assert the complete error boundary, including safe shape and cache controls.
    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string type)
    {
        Assert.Equal(status, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(new[] { "correlationId", "errors", "status", "title", "type" }, body.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal((int)status, body.GetProperty("status").GetInt32()); Assert.Equal(type, body.GetProperty("type").GetString()); Assert.False(string.IsNullOrEmpty(body.GetProperty("correlationId").GetString()));
    }
}
