using Dapper.Contrib.Extensions;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

[Route("[controller]")]
[ApiController]
public class RelationController : ControllerBase
{
    [HttpGet("{id}")]
    public IActionResult GetRelation(string id)
    {
        using var db = Config.GetDb();
        var relation = db.Get<RelationInfo>(id);
        if (relation == null)
            return NotFound();
        return Ok(relation);
    }

    [HttpPost]
    public IActionResult CreateRelation([FromBody] RelationInfo relation)
    {
        relation.Id = Guid.NewGuid().ToString();
        using var db = Config.GetDb();
        db.Insert(relation);
        return CreatedAtAction(nameof(GetRelation), new { id = relation.Id }, relation);
    }

    [HttpDelete("{id}")]
    public IActionResult DeleteRelation(string id)
    {
        using var db = Config.GetDb();
        var relation = db.Get<RelationInfo>(id);
        if (relation == null)
            return NotFound();
        db.Delete(relation);
        return NoContent();
    }
}