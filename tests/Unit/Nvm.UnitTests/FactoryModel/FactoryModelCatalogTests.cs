using Nvm.FactoryModel.Seeding;
using Nvm.FactoryModel.Storage;

namespace Nvm.UnitTests.FactoryModel;

/// <summary>
/// Chính bản thân cái shelf: điều gì được tính là một revision đã publish và một thư mục document
/// không được phép là gì. Mọi thứ ở đây được kiểm tra trong lúc container đang được build, nên mọi
/// lỗi đều fatal ngay lúc startup — đó chính là mục đích. Một service khởi động với một catalog đọc
/// dở dang sẽ từ chối activation cho những revision thực sự tồn tại, và lời từ chối đó sẽ trông như
/// một lỗi của operator.
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
        // Một caller làm việc dựa trên thông tin cũ và hỏi về một revision không tồn tại là chuyện
        // bình thường. Handler biến nó thành một lời từ chối có nêu tên cái shelf; còn catalog chỉ
        // đơn giản nói "không có ở đây".
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
        // Copy r2 thành r3 rồi quên đổi con số bên trong là lỗi dễ mắc nhất có thể xảy ra ở đây, và
        // cũng gây hại nhất: cây cũ sẽ được đưa vào force dưới một revision number mới, và mọi người
        // sẽ tin rằng một thay đổi đã xảy ra trong khi thực ra không có.
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
        // Bỏ qua âm thầm chính là cách một rollout đã publish biến mất lúc startup, và không ai phát
        // hiện ra cho tới ca làm việc mà plant được yêu cầu activate nó.
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
        // Revision 2, 3 và 4 đã được soạn ra nhưng chưa bao giờ publish. Từ chối điều đó sẽ tự đặt ra
        // một quy tắc mà business không có — các con số chỉ nói lên thứ tự các thay đổi đã xảy ra,
        // không phải mọi con số đều phải được dùng.
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
        // Cây nào đang in force khi đó sẽ phụ thuộc vào thứ tự load, và "the plant ran revision 4" sẽ
        // không còn là một fact duy nhất nữa.
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

    // Thứ nhỏ nhất mà parser chấp nhận như một plant. Các test này nói về cái shelf, không phải về
    // nội dung trên các trang.
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
