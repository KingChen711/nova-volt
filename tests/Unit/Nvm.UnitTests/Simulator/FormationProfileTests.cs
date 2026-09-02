using Nvm.Simulator.Formation;

namespace Nvm.UnitTests.Simulator;

/// <summary>Hình dạng của một formation cycle, và vì sao nó bắt buộc phải có hình dạng.</summary>
/// <remarks>
/// Các con số ngẫu nhiên sẽ khiến mọi thứ downstream trông như vẫn hoạt động trong khi không ai
/// nhận ra một projection đã bắt đầu tính sai đường cong — sẽ không có đường cong đúng nào để so
/// sánh, và sai sót sẽ ngủ yên cho tới M8. Các assertion này chính là thứ khiến đường cong thực sự
/// là một đường cong.
/// </remarks>
public sealed class FormationProfileTests
{
    private static readonly FormationProfile Profile = FormationProfile.Default;

    [Fact]
    public void ACycleRunsThroughItsFiveStagesInOrder()
    {
        var steps = Enumerable
            .Range(0, 1081)
            .Select(minute => Profile.At(TimeSpan.FromMinutes(minute)).Step)
            .ToList();

        steps.Distinct().ShouldBe(
        [
            FormationStep.RestBeforeCharge,
            FormationStep.ConstantCurrentCharge,
            FormationStep.ConstantVoltageCharge,
            FormationStep.RestAfterCharge,
            FormationStep.Discharge,
        ]);

        // Distinct() giữ nguyên thứ tự xuất hiện lần đầu, nên assertion ở trên đã nói "đúng thứ tự" —
        // nhưng chỉ khi không stage nào bị quay lại. Đây là nửa còn lại xác nhận điều đó.
        steps.Zip(steps.Skip(1)).ShouldAllBe(pair => pair.Second >= pair.First);
    }

    [Fact]
    public void TheVoltageClimbsThroughConstantCurrentAndStopsAtTheCeiling()
    {
        var climbing = Samples(TimeSpan.FromMinutes(20), TimeSpan.FromHours(7), TimeSpan.FromMinutes(10))
            .Select(sample => sample.Volts)
            .ToList();

        climbing.Zip(climbing.Skip(1)).ShouldAllBe(pair => pair.Second > pair.First);
        climbing[^1].ShouldBeLessThan(4.20);

        // Giữ phẳng suốt CV — đó chính là điều khiến nó là constant-voltage chứ không phải một đợt
        // leo dốc dài hơn.
        Samples(TimeSpan.FromHours(8.5), TimeSpan.FromHours(10.5), TimeSpan.FromMinutes(30))
            .Select(sample => sample.Volts)
            .Distinct()
            .Count()
            .ShouldBe(1);
    }

    [Fact]
    public void TheCurrentIsHeldThroughConstantCurrentAndTailsOffThroughConstantVoltage()
    {
        Samples(TimeSpan.FromMinutes(20), TimeSpan.FromHours(7), TimeSpan.FromHours(1))
            .Select(sample => sample.Amperes)
            .Distinct()
            .ShouldBe([0.5]);

        var tail = Samples(TimeSpan.FromHours(8), TimeSpan.FromHours(11), TimeSpan.FromMinutes(30))
            .Select(sample => sample.Amperes)
            .ToList();

        tail.Zip(tail.Skip(1)).ShouldAllBe(pair => pair.Second < pair.First);
        tail[^1].ShouldBeLessThan(0.1);
    }

    [Fact]
    public void TheCellRestsAtZeroCurrentAndDischargesBelowIt()
    {
        Profile.At(TimeSpan.FromMinutes(5)).Amperes.ShouldBe(0);
        Profile.At(TimeSpan.FromHours(11.2)).Amperes.ShouldBe(0);
        Profile.At(TimeSpan.FromHours(15)).Amperes.ShouldBeLessThan(0);
    }

    [Fact]
    public void CapacityRisesWhileChargingAndFallsWhileDischarging()
    {
        var charged = Profile.At(TimeSpan.FromHours(11.4)).AmpHours;

        Profile.At(TimeSpan.FromMinutes(5)).AmpHours.ShouldBe(0);
        charged.ShouldBeGreaterThan(3.5);

        // Khoảng nghỉ giữa charge và discharge không dịch chuyển điện tích nào cả, đúng như bản chất
        // của một rest.
        Profile.At(TimeSpan.FromHours(11.2)).AmpHours.ShouldBe(charged, 0.001);
        Profile.At(TimeSpan.FromHours(17)).AmpHours.ShouldBeLessThan(charged);
    }

    [Fact]
    public void TheCellWarmsWithTheCurrentAndSitsAtAmbientWhenItRests()
    {
        // Temperature trôi theo current chứ không theo đồng hồ. Một cell nóng lên khi đang nghỉ sẽ là
        // một cell có lỗi, và một simulator tạo ra điều đó sẽ tập cho mọi người ở downstream thói
        // quen bỏ qua tín hiệu này.
        Profile.At(TimeSpan.FromMinutes(5)).Celsius.ShouldBe(25);
        Profile.At(TimeSpan.FromHours(4)).Celsius.ShouldBeGreaterThan(30);
        Profile.At(TimeSpan.FromHours(11.2)).Celsius.ShouldBe(25);
    }

    [Fact]
    public void TheDischargeSitsOnAPlateauAndFallsAwayAtTheEnd()
    {
        // Cái "knee" là đặc trưng mà một grading rule tìm kiếm, nên một đường thẳng ở đây sẽ khiến
        // mọi grading test ở downstream pass trên dữ liệu chẳng có gì để chấm cả.
        var start = Profile.At(TimeSpan.FromHours(11.5));
        var middle = Profile.At(TimeSpan.FromHours(14.75));
        var end = Profile.At(TimeSpan.FromHours(18));

        var firstHalf = start.Volts - middle.Volts;
        var secondHalf = middle.Volts - end.Volts;

        secondHalf.ShouldBeGreaterThan(firstHalf * 2);
    }

    [Fact]
    public void ReadingOutsideTheCycleIsRefused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Profile.At(TimeSpan.FromMinutes(-1)));
        Should.Throw<ArgumentOutOfRangeException>(() => Profile.At(TimeSpan.FromHours(18.1)));
    }

    [Fact]
    public void ACycleShorterThanItsOwnStagesIsRefused()
    {
        // Các ranh giới stage là tuyệt đối, nên một cycle tám giờ sẽ đặt cell vào một discharge bắt
        // đầu sau khi cycle đã kết thúc.
        Should.Throw<ArgumentOutOfRangeException>(() => new FormationProfile(TimeSpan.FromHours(8)));
    }

    [Fact]
    public void TheProfileIsAFunctionOfElapsedTimeAndNothingElse()
    {
        // Tính chất mà toàn bộ lập luận về time-compression dựa vào. Nếu việc đọc profile phụ thuộc
        // vào số lần nó đã được đọc, việc nén một run sẽ làm thay đổi các measurement của nó.
        foreach (var minutes in new[] { 0, 14, 15, 300, 480, 660, 690, 1000, 1080 })
        {
            var elapsed = TimeSpan.FromMinutes(minutes);

            Profile.At(elapsed).ShouldBe(Profile.At(elapsed));
        }
    }

    private static IEnumerable<FormationSample> Samples(TimeSpan from, TimeSpan to, TimeSpan step)
    {
        for (var elapsed = from; elapsed <= to; elapsed += step)
        {
            yield return Profile.At(elapsed);
        }
    }
}
