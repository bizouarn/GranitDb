using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Descriptions.Base;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

[Route("[controller]")]
[ApiController]
public class TableInfoController : ControllerBase
{
    // Récupérer une table par son Id
    [HttpGet("{id}")]
    public async Task<IActionResult> GetTable(string id)
    {
        var table = await InfoBase.GetAsync<TableInfo>(id);
        if (table == null)
            return NotFound();
        table.Columns = (await InfoBase.GetAllAsync<ColumnInfo>()).Where(t => t.TableId == table.Id).ToList();
        table.Relations = (await InfoBase.GetAllAsync<RelationInfo>())
            .Where(r => table.Columns.Exists(c => c.Id == r.FirstId || c.Id == r.SecondId)).ToList();
        return Ok(table);
    }

    // Créer une nouvelle table
    [HttpPost]
    public async Task<IActionResult> CreateTable([FromBody] TableInfo table)
    {
        table = await InfoBase.CreateAsync(table);
        return CreatedAtAction(nameof(GetTable), new { id = table.Id }, table);
    }

    // Supprimer une table par son Id
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteTable(string id)
    {
        var table = await InfoBase.GetAsync<TableInfo>(id);
        if (table == null)
            return NotFound();
        await InfoBase.DeleteAsync(table);
        return NoContent();
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateTable(string id, [FromBody] TableInfo table)
    {
        if (id != table.Id)
            return BadRequest("ID mismatch");

        var existing = await InfoBase.GetAsync<TableInfo>(id);
        if (existing == null)
            return NotFound();

        await InfoBase.UpdateAsync(id, table);
        return Ok(table);
    }

    // Récupérer toutes les tables pour un DatabaseId donné
    [HttpGet("database/{databaseId}")]
    public async Task<IActionResult> GetTablesByDatabaseId(string databaseId)
    {
        var tables = (await InfoBase.GetAllAsync<TableInfo>()).Where(t => t.DatabaseId == databaseId).ToList();
        foreach (var table in tables)
        {
            table.Columns = (await InfoBase.GetAllAsync<ColumnInfo>()).Where(t => t.TableId == table.Id).ToList();
            table.Relations = (await InfoBase.GetAllAsync<RelationInfo>())
                .Where(r => table.Columns.Exists(c => c.Id == r.FirstId || c.Id == r.SecondId)).ToList();
        }

        if (tables.Count == 0)
            return NotFound();
        return Ok(tables);
    }
}