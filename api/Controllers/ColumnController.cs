using Dapper.Contrib.Extensions;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

[Route("[controller]")]
[ApiController]
public class ColumnController : ControllerBase
{
    [HttpGet("{id}")]
    public IActionResult GetColumn(string id)
    {
        using var db = Config.GetDb();
        var column = db.Get<ColumnInfo>(id);
        if (column == null)
            return NotFound();
        return Ok(column);
    }

    [HttpPost]
    public IActionResult CreateColumn([FromBody] ColumnInfo column)
    {
        column.Id = Guid.NewGuid().ToString();
        using var db = Config.GetDb();
        db.Insert(column);
        return CreatedAtAction(nameof(GetColumn), new { id = column.Id }, column);
    }

    [HttpDelete("{id}")]
    public IActionResult DeleteColumn(string id)
    {
        using var db = Config.GetDb();
        var column = db.Get<ColumnInfo>(id);
        if (column == null)
            return NotFound();
        db.Delete(column);
        return NoContent();
    }
}