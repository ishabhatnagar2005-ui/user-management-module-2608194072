// Compose a stateless JSON API; keep business routes in thin controllers.
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UserManagement.Api;
using UserManagement.Application;
using UserManagement.Audit;
using UserManagement.Infrastructure;
using UserManagement.Security;
var builder = WebApplication.CreateBuilder(args);
var security = SecurityOptions.Load(builder.Configuration);
builder.Services.AddSingleton(security);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<IdentifierProtector>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<ITokenService>(sp => sp.GetRequiredService<TokenService>());
builder.Services.AddSingleton<MongoStore>();
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<AuditRepository>();
builder.Services.AddSingleton<IndexMigration>();
builder.Services.AddSingleton<DevelopmentSeed>();
builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();
builder.Services.AddSingleton<UserValidator>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<DirectoryService>();
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.Configure<ApiBehaviorOptions>(o => o.InvalidModelStateResponseFactory = context =>
    new BadRequestObjectResult(new ApiError("validation_error", "Request validation failed", 400,
        context.ModelState.Where(x => x.Value?.Errors.Count > 0).ToDictionary(x => x.Key, _ => new[] { "Supply a valid value using only supported fields." }), context.HttpContext.TraceIdentifier)));
builder.Services.AddCors(o => o.AddPolicy("client", p =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    if (origins.Length > 0) p.WithOrigins(origins).WithMethods("GET", "POST", "PUT", "DELETE").WithHeaders("Authorization", "Content-Type");
}));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenService(security, TimeProvider.System).ValidationParameters();
    o.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            var principal = context.Principal!;
            if (!ObjectId.TryParse(principal.FindFirst("sub")?.Value, out var id) || !long.TryParse(principal.FindFirst("cv")?.Value, out var version)) { context.Fail("Invalid session"); return; }
            var user = await context.HttpContext.RequestServices.GetRequiredService<UserRepository>().FindByIdAsync(id, context.HttpContext.RequestAborted);
            if (user is null || !user.IsActive || user.CredentialVersion != version || !Enum.IsDefined(user.Role) || user.Role.ToString() != principal.FindFirst("role")?.Value) context.Fail("Invalid session");
        },
        OnChallenge = async context => { context.HandleResponse(); await ErrorMiddleware.WriteAsync(context.HttpContext, 401, "authentication_required", "Sign in is required", []); },
        OnForbidden = context => ErrorMiddleware.WriteAsync(context.HttpContext, 403, "access_denied", "Access is not permitted", [])
    };
});
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    o.AddPolicy("Admin", p => p.RequireAuthenticatedUser().RequireRole("Admin"));
});
var app = builder.Build();
app.UseMiddleware<ErrorMiddleware>();
app.UseCors("client");
app.UseMiddleware<AccountFailureAuditMiddleware>();
app.UseMiddleware<DirectoryFailureAuditMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
// Migrate and seed without accepting an unavailable dependency as successful startup.
await app.Services.GetRequiredService<IndexMigration>().ApplyAsync(CancellationToken.None);
await app.Services.GetRequiredService<UserRepository>().EnsureDirectoryIndexesAsync(CancellationToken.None);
await app.Services.GetRequiredService<DevelopmentSeed>().ApplyAsync(CancellationToken.None);
app.Run();
// Expose the application entry point to the real HTTP test host.
public partial class Program { }
