using Nvm.Time;

namespace Nvm.UnitTests.Time;

public sealed class ShiftScheduleTests
{
    [Fact]
    public void Default_CoversExactlyTwentyFourHours()
    {
        // Tính chất mà cả type này tồn tại để đảm bảo. Một bảng chỉ phủ 23 giờ sẽ để một giờ mỗi ngày
        // không thuộc về shift nào, và các reading đo được trong giờ đó sẽ không được ghi nhận vào
        // đâu cả — một khoảng trống không ai nhận ra cho tới khi tổng số cuối tháng bị thiếu.
        var covered = ShiftSchedule.Default.Definitions
            .Aggregate(TimeSpan.Zero, (total, definition) => total + definition.NominalLength);

        covered.ShouldBe(TimeSpan.FromHours(24));
    }

    [Fact]
    public void Default_IsTheTableFromScopeSection2Point3()
    {
        ShiftSchedule.Default.Definitions.ShouldBe(
        [
            new ShiftDefinition(Shift.A, new TimeOnly(6, 0), TimeSpan.FromHours(8)),
            new ShiftDefinition(Shift.B, new TimeOnly(14, 0), TimeSpan.FromHours(8)),
            new ShiftDefinition(Shift.C, new TimeOnly(22, 0), TimeSpan.FromHours(8)),
        ]);

        ShiftSchedule.Default.DayStart.ShouldBe(new TimeOnly(6, 0));
    }

    [Fact]
    public void Default_ShiftCIsTheOnlyOneThatCrossesMidnight()
    {
        // Mọi trường hợp rắc rối trong assembly này đều bắt nguồn từ đúng một sự thật này, nên nó
        // đáng được viết thành một assertion thay vì chỉ một comment: nếu một bảng trong tương lai
        // khiến hai shift cùng wrap qua nửa đêm, quy tắc production day ("ngày mà shift A bắt đầu")
        // sẽ không còn đủ để đặt tên cho một chu kỳ nữa.
        var wrapping = ShiftSchedule.Default.Definitions
            .Where(definition => definition.LocalStart.Add(definition.NominalLength) <= definition.LocalStart)
            .Select(definition => definition.Shift)
            .ToList();

        wrapping.ShouldBe([Shift.C]);
    }

    [Theory]
    [InlineData(6, 0, Shift.A)]
    [InlineData(13, 59, Shift.A)]
    [InlineData(14, 0, Shift.B)]
    [InlineData(21, 59, Shift.B)]
    [InlineData(22, 0, Shift.C)]
    [InlineData(23, 59, Shift.C)]
    [InlineData(0, 0, Shift.C)]
    [InlineData(5, 59, Shift.C)]
    public void ShiftAt_PutsEachClockReadingInOneShift(int hour, int minute, Shift expected)
    {
        ShiftSchedule.Default.ShiftAt(new TimeOnly(hour, minute)).Shift.ShouldBe(expected);
    }

    [Fact]
    public void ShiftAt_IsHalfOpenAtEveryBoundary()
    {
        // 14:00 thuộc về B, không thuộc cả A lẫn B. Sớm hơn một tick vẫn thuộc về A. Làm sai chỗ này
        // sẽ đếm trùng một reading cho mỗi lần đổi shift trên mỗi máy, đủ nhỏ để trông giống nhiễu
        // nhưng đủ lớn để làm lệch một con số yield.
        var schedule = ShiftSchedule.Default;
        var boundary = new TimeOnly(14, 0);

        schedule.ShiftAt(boundary).Shift.ShouldBe(Shift.B);
        schedule.ShiftAt(boundary.Add(TimeSpan.FromTicks(-1))).Shift.ShouldBe(Shift.A);
    }

    [Fact]
    public void OffsetIntoDay_MeasuresFromTheOpeningShift()
    {
        var schedule = ShiftSchedule.Default;

        schedule.OffsetIntoDay(Shift.A).ShouldBe(TimeSpan.Zero);
        schedule.OffsetIntoDay(Shift.B).ShouldBe(TimeSpan.FromHours(8));
        schedule.OffsetIntoDay(Shift.C).ShouldBe(TimeSpan.FromHours(16));
    }

    [Fact]
    public void Create_RefusesATableWithAGap()
    {
        // B bắt đầu một giờ sau khi A kết thúc. Giờ từ 13:00 tới 14:00 không thuộc về ai cả.
        var gapped = new[]
        {
            new ShiftDefinition(Shift.A, new TimeOnly(6, 0), TimeSpan.FromHours(7)),
            new ShiftDefinition(Shift.B, new TimeOnly(14, 0), TimeSpan.FromHours(8)),
            new ShiftDefinition(Shift.C, new TimeOnly(22, 0), TimeSpan.FromHours(8)),
        };

        Should.Throw<ArgumentException>(() => ShiftSchedule.Create(gapped));
    }

    [Fact]
    public void Create_RefusesATableThatOverlapsItself()
    {
        // A chạy chín giờ mà B vẫn bắt đầu lúc 14:00, nên 14:00–15:00 thuộc về hai shift. Tổng là 25
        // giờ, và đó chính là phép kiểm tra bắt được lỗi này.
        var overlapping = new[]
        {
            new ShiftDefinition(Shift.A, new TimeOnly(6, 0), TimeSpan.FromHours(9)),
            new ShiftDefinition(Shift.B, new TimeOnly(14, 0), TimeSpan.FromHours(8)),
            new ShiftDefinition(Shift.C, new TimeOnly(22, 0), TimeSpan.FromHours(8)),
        };

        Should.Throw<ArgumentException>(() => ShiftSchedule.Create(overlapping));
    }

    [Fact]
    public void Create_RefusesATableThatNamesAShiftTwice()
    {
        var duplicated = new[]
        {
            new ShiftDefinition(Shift.A, new TimeOnly(6, 0), TimeSpan.FromHours(12)),
            new ShiftDefinition(Shift.A, new TimeOnly(18, 0), TimeSpan.FromHours(12)),
        };

        Should.Throw<ArgumentException>(() => ShiftSchedule.Create(duplicated));
    }

    [Fact]
    public void Create_RefusesAnEmptyTable()
    {
        Should.Throw<ArgumentException>(() => ShiftSchedule.Create([]));
    }

    [Fact]
    public void Create_AcceptsATableThatIsNotThreeEightHourShifts()
    {
        // M10 mang tới một site với một bảng khác, và type này phải có khả năng chứa được nó. Hai
        // shift mười hai giờ bắt đầu lúc 07:00 — một pattern có thật, và nó kéo theo cả boundary của
        // production day di chuyển theo.
        var twelves = ShiftSchedule.Create(
        [
            new ShiftDefinition(Shift.A, new TimeOnly(7, 0), TimeSpan.FromHours(12)),
            new ShiftDefinition(Shift.B, new TimeOnly(19, 0), TimeSpan.FromHours(12)),
        ]);

        twelves.DayStart.ShouldBe(new TimeOnly(7, 0));
        twelves.ShiftAt(new TimeOnly(6, 59)).Shift.ShouldBe(Shift.B);
        twelves.ShiftAt(new TimeOnly(7, 0)).Shift.ShouldBe(Shift.A);
    }

    [Fact]
    public void Definition_RefusesAShiftThePlantDoesNotRun()
    {
        var twelves = ShiftSchedule.Create(
        [
            new ShiftDefinition(Shift.A, new TimeOnly(7, 0), TimeSpan.FromHours(12)),
            new ShiftDefinition(Shift.B, new TimeOnly(19, 0), TimeSpan.FromHours(12)),
        ]);

        Should.Throw<ArgumentOutOfRangeException>(() => twelves.Definition(Shift.C));
    }
}
