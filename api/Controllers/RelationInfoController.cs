using GranitDB.API.Controllers.Base;
using GranitDB.API.Models.Descriptions;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

[Route("[controller]")]
[ApiController]
public class RelationInfoController : BaseDbInfoController<RelationInfo>
{
}