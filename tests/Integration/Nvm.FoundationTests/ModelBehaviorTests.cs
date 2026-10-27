using Nvm.Foundation.Runtime;
using Nvm.FoundationTests.LabModel;

namespace Nvm.FoundationTests;

/// <summary>Domain, cột hệ thống, xóa mềm, facet (DoD R1 ý 6 và bảng cơ chế scope §3.1).</summary>
public sealed class ModelBehaviorTests
{
    [Fact]
    public async Task Delete_then_recreate_with_the_same_NId_succeeds()
    {
        var h = await FoundationHarness.CreateAsync();
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });
        h.Run(new CreateLabOrder { NId = "PAL-1", MaterialNId = "MAT-1", Quantity = 16 }).StatusCode.ShouldBe(200);

        h.Run(new DeleteLabOrder { NId = "PAL-1" }).StatusCode.ShouldBe(200);
        h.Run(new CreateLabOrder { NId = "PAL-1", MaterialNId = "MAT-1", Quantity = 16 }).StatusCode.ShouldBe(200);
        h.Run(new DeleteLabOrder { NId = "PAL-1" }).StatusCode.ShouldBe(200);
        h.Run(new CreateLabOrder { NId = "PAL-1", MaterialNId = "MAT-1", Quantity = 16 }).StatusCode.ShouldBe(200);

        // Mỗi bản đã xóa một số riêng (lớn nhất + 1), bản sống là 0.
        var table = h.TableOf<LabOrder>();
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{table}] WHERE NId = 'PAL-1'").ShouldBe(3);
        h.Scalar<int>($"SELECT COUNT(DISTINCT IsDeleted) FROM dbo.[{table}] WHERE NId = 'PAL-1'").ShouldBe(3);
        h.Scalar<int>($"SELECT MAX(IsDeleted) FROM dbo.[{table}]").ShouldBe(2);
    }

    [Fact]
    public async Task Sql_join_without_IsDeleted_counts_deleted_rows_lab_L03()
    {
        var h = await FoundationHarness.CreateAsync();
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });
        h.Run(new CreateLabOrder { NId = "PAL-1", MaterialNId = "MAT-1", Quantity = 16 });
        h.Run(new DeleteLabOrder { NId = "PAL-1" });
        h.Run(new CreateLabOrder { NId = "PAL-1", MaterialNId = "MAT-1", Quantity = 16 });
        var orders = h.TableOf<LabOrder>();
        var materials = h.TableOf<LabMaterial>();

        // JOIN theo NId chỉ lọc bảng chính: bản đã xóa của bảng kia lọt vào, một pallet ra hai dòng.
        h.Scalar<int>($"""
            SELECT COUNT(*) FROM dbo.[{materials}] m
            JOIN dbo.[{orders}] o ON o.MaterialNId = m.NId
            WHERE m.IsDeleted = 0 AND o.NId = 'PAL-1'
            """).ShouldBe(2);
        h.Scalar<int>($"""
            SELECT COUNT(*) FROM dbo.[{materials}] m
            JOIN dbo.[{orders}] o ON o.MaterialNId = m.NId AND o.IsDeleted = 0
            WHERE m.IsDeleted = 0 AND o.NId = 'PAL-1'
            """).ShouldBe(1);
    }

    [Fact]
    public async Task Query_sees_submitted_changes_but_projection_query_does_not()
    {
        var h = await FoundationHarness.CreateAsync();

        var result = h.Run(new CreateAndCountLabOrders { NId = "ORD-1" });

        result.As<CountResponse>().InTransaction.ShouldBe(1);
        result.As<CountResponse>().Projection.ShouldBe(0);
    }

    [Fact]
    public async Task Query_of_another_domain_is_refused()
    {
        var h = await FoundationHarness.CreateAsync();

        var result = h.Run(new ReadMaterialWithQuery { MaterialNId = "MAT-1" });

        result.StatusCode.ShouldBe(400);
        result.Response!.Error!.ErrorMessage.ShouldContain("Use ProjectionQuery");
    }

    [Fact]
    public async Task Mandatory_entity_property_is_enforced_by_the_model_while_the_column_stays_nullable()
    {
        var h = await FoundationHarness.CreateAsync();

        var result = h.Run(new CreateLabMaterial { NId = "MAT-1" });

        result.StatusCode.ShouldBe(400);
        result.Response!.Error!.ErrorMessage.ShouldBe("LabMaterial.UnitOfMeasure is mandatory.");
        h.Scalar<string>($"""
            SELECT is_nullable = CAST(is_nullable AS nvarchar(1)) FROM sys.columns
            WHERE object_id = OBJECT_ID('dbo.[{h.TableOf<LabMaterial>()}]') AND name = 'UnitOfMeasure'
            """).ShouldBe("1");
    }

    [Fact]
    public async Task System_columns_are_stamped_from_the_platform_clock()
    {
        var h = await FoundationHarness.CreateAsync();
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });

        h.Scalar<DateTimeOffset>($"SELECT CreatedOn FROM dbo.[{h.TableOf<LabMaterial>()}]").ShouldBe(h.Time.GetUtcNow());
    }

    [Fact]
    public async Task Facets_of_an_entity_share_one_table_with_sparse_columns()
    {
        var h = await FoundationHarness.CreateAsync();
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });

        h.Run(new RegisterLabOrderWithSupplier { NId = "ORD-1", MaterialNId = "MAT-1", SupplierCode = "SUP-7" }).StatusCode.ShouldBe(200);

        h.TableOf<LabOrderCounters>().ShouldBe(h.TableOf<LabOrderSupplier>());
        var table = h.TableOf<LabOrderSupplier>();
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{table}]").ShouldBe(2);
        h.Scalar<string>($"SELECT LabOrderSupplier_SupplierCode FROM dbo.[{table}] WHERE FacetType = 'LabOrderSupplier'").ShouldBe("SUP-7");
        h.Scalar<int>($"""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID('dbo.[{table}]') AND is_sparse = 1
            """).ShouldBe(2);
        h.Scalar<int>($"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.[{table}]') AND name = 'LabOrder_Id'").ShouldBe(1);
    }

    [Fact]
    public async Task Table_names_are_truncated_with_a_numeric_suffix_and_recorded()
    {
        var h = await FoundationHarness.CreateAsync();

        h.TableOf<LabOrderNote>().ShouldMatch(@"^LabOrderNote_\d{1,9}$");
        h.Scalar<string>($"SELECT TableName FROM dbo.{SchemaUpdater.ModelTable} WHERE ModelName = 'FB_OP_Lab.LabOrderNote'")
            .ShouldBe(h.TableOf<LabOrderNote>());
    }

    [Fact]
    public void Pre_check_in_another_domain_is_a_model_error()
    {
        Should.Throw<InvalidOperationException>(() => ModelRegistry.Build(FoundationHarness.LabTypes.Append(typeof(ModelErrors.MisplacedPreCheck))))
            .Message.ShouldContain("must be in the domain of");
    }
}
