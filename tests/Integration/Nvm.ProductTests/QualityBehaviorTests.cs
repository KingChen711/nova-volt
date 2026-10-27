using Nvm.Product;

namespace Nvm.ProductTests;

/// <summary>NC, giữ và thả hàng, containment request.</summary>
public sealed class QualityBehaviorTests
{
    private static async Task<ProductHarness> WithModulesAsync(params string[] nids)
    {
        var p = await ProductHarness.CreateAsync();
        foreach (var nid in nids)
        {
            p.Ok(new CreateMtu { NId = nid, MaterialNId = ProductHarness.Module, EquipmentNId = ProductHarness.Machine });
        }

        return p;
    }

    [Fact]
    public async Task Open_nc_holds_the_mtu_and_scrap_disposition_scraps_it()
    {
        var p = await WithModulesAsync("M-1");
        p.Ok(new DefineDefect { NId = "Z", Description = "OTHER" });

        p.Ok(new CreateNonConformance { NId = "NC-1", MtuNIds = ["M-1"], DefectNId = "Z", Type = "QUALITY", Severity = "MEDIUM" });
        p.Mtu("M-1").Status.ShouldBe(MtuStatus.OnHold);

        p.Ok(new SetNonConformanceStatus { NId = "NC-1", Status = NonConformanceStatus.Scrap });
        p.Mtu("M-1").Status.ShouldBe(MtuStatus.Scrapped);
        p.Read<NonConformance>().Single().EndDate.ShouldNotBeNull();
        p.Fails(new SetNonConformanceStatus { NId = "NC-1", Status = NonConformanceStatus.UseAsIs }, ProductErrors.InvalidNonConformanceStatus);
    }

    [Fact]
    public async Task Use_as_is_releases_the_mtu_only_when_its_last_open_nc_closes()
    {
        var p = await WithModulesAsync("M-1");
        p.Ok(new CreateNonConformance { NId = "NC-1", MtuNIds = ["M-1"] });
        p.Ok(new CreateNonConformance { NId = "NC-2", MtuNIds = ["M-1"] });

        p.Ok(new SetNonConformanceStatus { NId = "NC-1", Status = NonConformanceStatus.UseAsIs });
        p.Mtu("M-1").Status.ShouldBe(MtuStatus.OnHold);

        p.Ok(new SetNonConformanceStatus { NId = "NC-2", Status = NonConformanceStatus.UseAsIs });
        p.Mtu("M-1").Status.ShouldBe(MtuStatus.Available);
    }

    [Fact]
    public async Task Scrap_at_creation_scraps_the_list_and_leaves_the_existing_open_nc_open()
    {
        var p = await WithModulesAsync("M-1", "M-2");
        p.Ok(new CreateNonConformance { NId = "NC-1", MtuNIds = ["M-1"] });

        p.Ok(new CreateNonConformance { NId = "NC-2", MtuNIds = ["M-1", "M-2"], Status = NonConformanceStatus.Scrap });

        p.Mtu("M-1").Status.ShouldBe(MtuStatus.Scrapped);
        p.Mtu("M-2").Status.ShouldBe(MtuStatus.Scrapped);
        p.Read<NonConformance>(q => q.Where(n => n.NId == "NC-1")).Single().Status.ShouldBe(NonConformanceStatus.Open);
        p.Ok(new SetNonConformanceStatus { NId = "NC-1", Status = NonConformanceStatus.UseAsIs });
        p.Mtu("M-1").Status.ShouldBe(MtuStatus.Scrapped);
    }

    [Fact]
    public async Task Scrapped_mtu_is_rejected_by_a_new_nc_and_by_hold()
    {
        var p = await WithModulesAsync("M-1", "M-2");
        p.Ok(new CreateNonConformance { NId = "NC-1", MtuNIds = ["M-1"], Status = NonConformanceStatus.Scrap });

        p.Fails(new CreateNonConformance { NId = "NC-2", MtuNIds = ["M-2", "M-1"] }, ProductErrors.MtuScrapped);
        p.Mtu("M-2").Status.ShouldBe(MtuStatus.Available);
        p.Fails(new HoldMtus { MtuNIds = ["M-1"] }, ProductErrors.MtuScrapped);
    }

    [Fact]
    public async Task Hold_and_release_a_list_but_not_an_mtu_with_an_open_nc()
    {
        var p = await WithModulesAsync("M-1", "M-2");
        p.Ok(new HoldMtus { MtuNIds = ["M-1", "M-2"] });
        p.Mtu("M-2").Status.ShouldBe(MtuStatus.OnHold);

        p.Ok(new ReleaseMtus { MtuNIds = ["M-1", "M-2"] });
        p.Mtu("M-1").Status.ShouldBe(MtuStatus.Available);

        p.Ok(new CreateNonConformance { NId = "NC-1", MtuNIds = ["M-2"] });
        p.Fails(new ReleaseMtus { MtuNIds = ["M-2"] }, ProductErrors.MtuNotReleasable);
    }

    [Fact]
    public async Task Importing_containment_numbers_it_holds_and_flags_the_mtus_and_raises_an_event()
    {
        var p = await WithModulesAsync("M-1", "M-2");
        p.Ok(new CreateNonConformance { NId = "NC-1", MtuNIds = ["M-2"], Status = NonConformanceStatus.Scrap });

        var cr = p.Ok(new ImportContainmentRequest { Name = "QT-0042 suspect weld", SourceSystem = "QualityTrace", MtuNIds = ["M-1", "M-2"] });
        cr.NId.ShouldBe("ContainmentRequest_000001");

        p.Read<ContainmentRequest>().Single().Status.ShouldBe(ContainmentStatus.Ready);
        p.Mtu("M-1").Status.ShouldBe(MtuStatus.OnHold);
        p.Mtu("M-1").InContainment.ShouldBeTrue();
        p.Mtu("M-2").Status.ShouldBe(MtuStatus.Scrapped);
        p.Mtu("M-2").InContainment.ShouldBeTrue();
        p.Events.Published.Select(e => e.Event).OfType<ContainmentRequestImported>().ShouldHaveSingleItem().MtuCount.ShouldBe(2);

        p.Ok(new ImportContainmentRequest { Name = "empty" }).NId.ShouldBe("ContainmentRequest_000002");
        p.Read<ContainmentRequest>(q => q.Where(c => c.Name == "empty")).Single().Status.ShouldBe(ContainmentStatus.Editing);
    }

    [Fact]
    public async Task Release_frees_the_group_but_close_keeps_the_goods_held()
    {
        var p = await WithModulesAsync("M-1", "M-2");
        var first = p.Ok(new ImportContainmentRequest { MtuNIds = ["M-1"] }).NId!;
        var second = p.Ok(new ImportContainmentRequest { MtuNIds = ["M-2"] }).NId!;

        p.Ok(new ReleaseContainmentRequest { NId = first });
        p.Mtu("M-1").Status.ShouldBe(MtuStatus.Available);
        p.Mtu("M-1").InContainment.ShouldBeFalse();

        p.Ok(new CloseContainmentRequest { NId = second });
        p.Mtu("M-2").Status.ShouldBe(MtuStatus.OnHold);
        p.Mtu("M-2").InContainment.ShouldBeTrue();
        p.Fails(new ReleaseContainmentRequest { NId = second }, ProductErrors.ContainmentClosed);
    }
}
