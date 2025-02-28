using GranitDB.API.Controllers.Base;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Descriptions.Base;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

[Route("[controller]")]
[ApiController]
public class DatabaseInfoController : BaseDbInfoController<DatabaseInfo>
{
    [HttpGet]
    public async Task<IActionResult> GetAllDatabase()
    {
        var database = await InfoBase.GetAllAsync<DatabaseInfo>();
        if (database == null)
            return NotFound();
        return Ok(database);
    }
}