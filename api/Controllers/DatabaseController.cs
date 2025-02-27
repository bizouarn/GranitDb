using Dapper.Contrib.Extensions;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

[Route("[controller]")]
[ApiController]
public class DatabaseController : ControllerBase
{
    [HttpGet("{id}")]
    public IActionResult GetDatabase(string id)
    {
        using var db = Config.GetDb();
        var database = db.Get<DatabaseInfo>(id);
        if (database == null)
            return NotFound();
        return Ok(database);
    }

    [HttpGet]
    public IActionResult GetDatabase()
    {
        using var db = Config.GetDb();
        var database = db.GetAll<DatabaseInfo>();
        if (database == null)
            return NotFound();
        return Ok(database);
    }

    [HttpPost]
    public IActionResult CreateDatabase([FromBody] DatabaseInfo database)
    {
        database.Id = Guid.NewGuid().ToString();
        using var db = Config.GetDb();
        db.Insert(database);
        return CreatedAtAction(nameof(GetDatabase), new { id = database.Id }, database);
    }

    [HttpPut("{id}")]
    public IActionResult UpdateDatabase(string id, [FromBody] DatabaseInfo database)
    {
        if (id != database.Id)
            return BadRequest("ID mismatch");

        using var db = Config.GetDb();
        var existing = db.Get<DatabaseInfo>(id);
        if (existing == null)
            return NotFound();

        db.Update(database);
        return Ok(database);
    }

    [HttpDelete("{id}")]
    public IActionResult DeleteDatabase(string id)
    {
        using var db = Config.GetDb();
        var database = db.Get<DatabaseInfo>(id);
        if (database == null)
            return NotFound();

        db.Delete(database);
        return NoContent();
    }
}