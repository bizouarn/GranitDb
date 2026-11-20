using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers.Base;

public static class ControllerExtensions
{
    /// <summary>La réponse 200 avec la valeur, ou 404 quand elle est absente (élément introuvable).</summary>
    public static IActionResult OkOrNotFound(this ControllerBase controller, object? value) =>
        value == null ? controller.NotFound() : controller.Ok(value);
}
