using GranitDB.API.Models.Descriptions.Base;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers.Base;

public class BaseDbInfoController<T> : ControllerBase where T : InfoBase
{
    [HttpGet("{id}")]
    public async Task<IActionResult> GetT(string id)
    {
        var info = await InfoBase.GetAsync<T>(id);
        if (info == null)
            return NotFound();
        return Ok(info);
    }

    [HttpPost]
    public async Task<IActionResult> CreateT([FromBody] T info)
    {
        info = await InfoBase.CreateAsync(info);
        return CreatedAtAction(nameof(GetT), new { id = info.GetId() }, info);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateT(string id, [FromBody] T info)
    {
        if (id != info.GetId())
            return BadRequest("ID mismatch");

        var existing = await InfoBase.GetAsync<T>(id);
        if (existing == null)
            return NotFound();

        await InfoBase.UpdateAsync(id, info);
        return Ok(info);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteT(string id)
    {
        await InfoBase.DeleteAsync<T>(id);
        return NoContent();
    }
}