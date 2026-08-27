using Nvm.FactoryModel.Seeding;
using Nvm.FactoryModel.Storage;

namespace Nvm.UnitTests.FactoryModel;

/// <summary>
/// The shelf itself: what counts as a published revision and what a directory of documents is not
/// allowed to be. Everything here is checked while the container is being built, so every failure is
/// fatal at startup — which is the point. A service that came up on a half-read catalog would refuse
/// activations for revisions that exist, and the refusal would look like an operator error.
/// </summary>
public sealed class FactoryModelCatalogTests
{
    private static readonly string SeedDirectory = Path.Combine(AppContext.BaseDirectory, "seed");

    [Fact]
    public void LoadCatalog_ReadsEveryPublishedRevisionInTheDirectory()
    {
        var catalog = FactoryModelSeed.LoadCatalog(SeedDirectory);

        catalog.Revisions.ShouldBe([1, 2, 3]);
        catalog.LatestRevision.ShouldBe(3);
        catalog.Find(2).ShouldNotBeNull().Revision.ShouldBe(2);
    }

    [Fact]
    public void Find_ARevisionThatWasNeverPublished_ReturnsNullRatherThanThrowing()
    {
        // A caller working from stale information asking for a revision that does not exist is normal.
        // The handler turns it into a refusal that names the shelf; the catalog just says "not here".
        FactoryModelSeed.LoadCatalog(SeedDirectory).Find(99).ShouldBeNull();
    }

    [Fact]
    public void LoadCatalog_ADirectoryHoldingNoDocument_IsRejected()
    {
        InATemporaryDirectory(directory =>
        {
            var thrown = Should.Throw<FactoryModelSeedException>(
                () => FactoryModelSeed.LoadCatalog(directory));

            thrown.Message.ShouldContain(FactoryModelSeed.FileNameFor(1));
        });
    }

    [Fact]
    public void LoadCatalog_ADirectoryThatIsNotThere_IsRejected()
    {
        Should.Throw<DirectoryNotFoundException>(
            () => FactoryModelSeed.LoadCatalog(Path.Combine(SeedDirectory, "no-such-shelf")));
    }

    [Fact]
    public void LoadCatalog_AFileNameThatDisagreesWithTheDocumentInside_IsRejected()
    {
        // Copying r2 to r3 and forgetting to change the number inside is the easiest mistake available
        // here, and the most damaging: the old tree would go into force under a new revision number,
        // and everyone would believe a change happened that did not.
        InATemporaryDirectory(directory =>
        {
            WriteDocument(directory, FactoryModelSeed.FileNameFor(3), revision: 2);

            var thrown = Should.Throw<FactoryModelSeedException>(
                () => FactoryModelSeed.LoadCatalog(directory));

            thrown.Message.ShouldContain(FactoryModelSeed.FileNameFor(3));
            thrown.Message.ShouldContain("revision 2");
        });
    }

    [Fact]
    public void LoadCatalog_AFileNameThatIsNotARevision_IsRejectedRatherThanSkipped()
    {
        // Skipping quietly is how a published rollout disappears at startup, and nobody finds out
        // until the shift the plant is asked to activate it.
        InATemporaryDirectory(directory =>
        {
            WriteDocument(directory, FactoryModelSeed.FileNameFor(1), revision: 1);
            WriteDocument(directory, "factory-model.rDRAFT.json", revision: 1);

            var thrown = Should.Throw<FactoryModelSeedException>(
                () => FactoryModelSeed.LoadCatalog(directory));

            thrown.Message.ShouldContain("rDRAFT");
        });
    }

    [Fact]
    public void LoadCatalog_RevisionNumbersWithGaps_AreAccepted()
    {
        // Revisions 2, 3 and 4 were drafted and never published. Refusing that would invent a rule the
        // business does not have — the numbers say what order changes happened in, not that every
        // number was used.
        InATemporaryDirectory(directory =>
        {
            WriteDocument(directory, FactoryModelSeed.FileNameFor(1), revision: 1);
            WriteDocument(directory, FactoryModelSeed.FileNameFor(5), revision: 5);

            var catalog = FactoryModelSeed.LoadCatalog(directory);

            catalog.Revisions.ShouldBe([1, 5]);
            catalog.LatestRevision.ShouldBe(5);
        });
    }

    [Fact]
    public void Catalog_TwoDocumentsClaimingOneRevision_IsRejected()
    {
        // Which tree was in force would then depend on load order, and "the plant ran revision 4" would
        // stop being a single fact.
        var one = FactoryModelSeed.Parse(DocumentJson(4));
        var other = FactoryModelSeed.Parse(DocumentJson(4));

        Should.Throw<ArgumentException>(() => new InMemoryFactoryModelCatalog([one, other]));
    }

    [Fact]
    public void Catalog_WithNoRevisionAtAll_IsRejected()
    {
        Should.Throw<ArgumentException>(() => new InMemoryFactoryModelCatalog([]));
    }

    private static void InATemporaryDirectory(Action<string> body)
    {
        var directory = Directory.CreateTempSubdirectory("nvm-catalog-").FullName;

        try
        {
            body(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteDocument(string directory, string fileName, int revision) =>
        File.WriteAllText(Path.Combine(directory, fileName), DocumentJson(revision));

    // The smallest thing the parser accepts as a plant. These tests are about the shelf, not about
    // what is on the pages.
    private static string DocumentJson(int revision) => $$"""
        {
          "revision": {{revision}},
          "generatedAt": "2026-08-26T09:00:00+00:00",
          "enterprise": {
            "code": "NOVAVOLT",
            "name": "NovaVolt Energy",
            "children": [
              { "code": "NV1", "name": "Hai Phong Gigafactory", "timeZoneId": "Asia/Ho_Chi_Minh" }
            ]
          }
        }
        """;
}
