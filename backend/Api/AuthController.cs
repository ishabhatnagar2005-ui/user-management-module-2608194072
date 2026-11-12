// Expose only the approved authentication and readiness contracts in this slice.
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UserManagement.Application;
using UserManagement.Infrastructure;
using UserManagement.Security;
namespace UserManagement.Api;
[ApiController]
[Route("api")]
public sealed class AuthController(IAuthenticationService authentication, MongoStore store) : ControllerBase
{
    // Delegate credential policy and audit durability to the application service.
    [AllowAnonymous, HttpPost("login")]
    public async Task<ActionResult<LoginResult>> Login([FromBody] LoginRequest request, CancellationToken ct) => Ok(await authentication.SignInAsync(request, ct));
    // Report dependency readiness without exposing topology or connection credentials.
    [AllowAnonymous, HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken ct) { await store.PingAsync(ct); return Ok(new { status = "healthy" }); }
}
