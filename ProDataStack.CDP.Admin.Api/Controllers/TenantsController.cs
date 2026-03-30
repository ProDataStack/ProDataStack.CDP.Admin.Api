using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ProDataStack.CDP.Admin.Api.Controllers
{
    [ApiController]
    [Route("api/v1/[controller]")]
    public class TenantsController : ControllerBase
    {
        [HttpGet("/api/v1/health-check")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult HealthCheck()
        {
            return Ok(new { status = "ok", service = "CDP Admin API" });
        }

        [HttpGet]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult GetTenants()
        {
            return Ok(new { message = "Admin API scaffold — tenant endpoints coming soon" });
        }
    }
}
