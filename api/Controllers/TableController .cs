using Dapper.Contrib.Extensions;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

[Route("[controller]")]
[ApiController]
public class TableController : ControllerBase
{
    // Récupérer une table par son Id
    [HttpGet("{id}")]
    public IActionResult GetTable(string id)
    {
        using var db = Config.GetDb();
        var table = db.Get<TableInfo>(id);
        table.Columns = db.GetAll<ColumnInfo>().Where(t => t.TableId == table.Id).ToList();
        table.Relations = db.GetAll<RelationInfo>()
            .Where(r => table.Columns.Exists(c => c.Id == r.FirstId || c.Id == r.SecondId)).ToList();
        if (table == null)
            return NotFound();
        return Ok(table);
    }

    // Créer une nouvelle table
    [HttpPost]
    public IActionResult CreateTable([FromBody] TableInfo table)
    {
        table.Id = Guid.NewGuid().ToString(); // Assurez-vous que l'ID est généré
        using var db = Config.GetDb();
        db.Insert(table);
        return CreatedAtAction(nameof(GetTable), new { id = table.Id }, table);
    }

    // Supprimer une table par son Id
    [HttpDelete("{id}")]
    public IActionResult DeleteTable(string id)
    {
        using var db = Config.GetDb();
        var table = db.Get<TableInfo>(id);
        if (table == null)
            return NotFound();
        db.Delete(table);
        return NoContent();
    }

    // Récupérer toutes les tables pour un DatabaseId donné
    [HttpGet("database/{databaseId}")]
    public IActionResult GetTablesByDatabaseId(string databaseId)
    {
        using var db = Config.GetDb();
        var tables = db.GetAll<TableInfo>().Where(t => t.DatabaseId == databaseId).ToList();
        foreach (var table in tables)
        {
            table.Columns = db.GetAll<ColumnInfo>().Where(t => t.TableId == table.Id).ToList();
            table.Relations = db.GetAll<RelationInfo>()
                .Where(r => table.Columns.Exists(c => c.Id == r.FirstId || c.Id == r.SecondId)).ToList();
        }

        if (tables.Count == 0)
            return NotFound();
        return Ok(tables);
    }
}