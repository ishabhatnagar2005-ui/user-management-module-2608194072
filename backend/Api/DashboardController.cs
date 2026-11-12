// Expose the approved administrator-only activity read without client identity or controls.
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UserManagement.Application;
namespace UserManagement.Api;
// Delegate source access and validation to the application service.
[ApiController, Route("api/dashboard"), Authorize(Policy = "Admin")]
public sealed class DashboardController(DashboardService service) : ControllerBase
{
    // Return only safe activity data; authorization runs before database reads.
    [HttpGet]
    public async Task<ActionResult<DashboardView>> Read(CancellationToken ct) => Ok(await service.ReadAsync(Request.Query, ct));
}
