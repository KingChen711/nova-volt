using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Nvm.PublicObjectModel;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class EquipmentController(PomReadDbContext database) : ODataController
{
    private const AllowedQueryOptions ReadOptions = AllowedQueryOptions.Filter | AllowedQueryOptions.Select
        | AllowedQueryOptions.OrderBy | AllowedQueryOptions.Top | AllowedQueryOptions.Skip | AllowedQueryOptions.Count;

    [EnableQuery(PageSize = 50, MaxTop = 1000, MaxNodeCount = 100, AllowedQueryOptions = ReadOptions)]
    public IQueryable<Equipment> Get() => database.Equipment.AsNoTracking().OrderBy(row => row.Id);

    [EnableQuery(AllowedQueryOptions = AllowedQueryOptions.Select)]
    public async Task<IActionResult> Get(string key, CancellationToken cancellationToken)
    {
        // Chỉ xét conditional sau lookup đã scope; ETag không được làm lộ key của site khác.
        var entity = await database.Equipment.SingleOrDefaultAsync(row => row.Id == key, cancellationToken);
        if (entity is null)
        {
            return NotFound();
        }

        var etag = EntityTagHeaderValue.Parse(Request.CreateETag(
            new Dictionary<string, object> { [nameof(Equipment.Revision)] = entity.Revision }));
        Response.GetTypedHeaders().ETag = etag;
        if (Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var conditions))
        {
            if (!EntityTagHeaderValue.TryParseList(conditions.Select(value => value ?? string.Empty).ToArray(), out var parsed))
            {
                return BadRequest();
            }

            if (parsed.Any(candidate => candidate == EntityTagHeaderValue.Any || candidate.Compare(etag, useStrongComparison: false)))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }
        }

        return Ok(entity);
    }
}
