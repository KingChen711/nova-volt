using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Nvm.PublicObjectModel;

// Lặp lại có chủ đích pattern EquipmentController thay vì gom về một controller generic: mỗi entity set
// nhỏ, đọc được một mình, và một khung chung sẽ giấu đúng chỗ site filter/paging cần nhìn thấy rõ.
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ProductionUnitsController(PomReadDbContext database) : ODataController
{
    private const AllowedQueryOptions ReadOptions = AllowedQueryOptions.Filter | AllowedQueryOptions.Select
        | AllowedQueryOptions.OrderBy | AllowedQueryOptions.Top | AllowedQueryOptions.Skip | AllowedQueryOptions.Count;

    [EnableQuery(PageSize = 50, MaxTop = 1000, MaxNodeCount = 100, AllowedQueryOptions = ReadOptions)]
    public IQueryable<ProductionUnit> Get() => database.ProductionUnits.AsNoTracking().OrderBy(row => row.Id);

    [EnableQuery(AllowedQueryOptions = AllowedQueryOptions.Select)]
    public async Task<IActionResult> Get(string key, CancellationToken cancellationToken)
    {
        // Site filter chạy trong query nên key của site khác trả null ở đây, thành 404 trước khi tính ETag.
        var entity = await database.ProductionUnits.SingleOrDefaultAsync(row => row.Id == key, cancellationToken);
        if (entity is null)
        {
            return NotFound();
        }

        var etag = EntityTagHeaderValue.Parse(Request.CreateETag(
            new Dictionary<string, object> { [nameof(ProductionUnit.Revision)] = entity.Revision }));
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
