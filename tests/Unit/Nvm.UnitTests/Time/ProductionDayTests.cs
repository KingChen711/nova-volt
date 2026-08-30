using System.Reflection;
using Nvm.Time;

namespace Nvm.UnitTests.Time;

public sealed class ProductionDayTests
{
    [Fact]
    public void ProductionDay_HasNoConversionToOrFromACalendarDate()
    {
        // The entire reason the type exists. DateOnly and ProductionDay hold the same three numbers,
        // so a conversion operator — in either direction — would let `CAST(device_timestamp AS date)`
        // be assigned to a production day with the compiler's blessing. That is wrong for six hours
        // out of every twenty-four, and it is wrong silently.
        var conversions = typeof(ProductionDay)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name is "op_Implicit" or "op_Explicit")
            .Select(method => $"{method.Name} -> {method.ReturnType.Name}")
            .ToList();

        conversions.ShouldBeEmpty(
            $"ProductionDay must stay unconvertible; it declares {string.Join(", ", conversions)}");
    }

    [Fact]
    public void On_KeepsTheDateItWasNamedBy()
    {
        ProductionDay.On(2026, 8, 25).Date.ShouldBe(new DateOnly(2026, 8, 25));
        ProductionDay.On(new DateOnly(2026, 8, 25)).ShouldBe(ProductionDay.On(2026, 8, 25));
    }

    [Fact]
    public void NextAndPrevious_StepOneCalendarDate()
    {
        var day = ProductionDay.On(2026, 2, 28);

        day.Next().ShouldBe(ProductionDay.On(2026, 3, 1));
        day.Next().Previous().ShouldBe(day);
    }

    [Fact]
    public void DaysUntil_CountsForwardAndBackward()
    {
        ProductionDay.On(2026, 3, 28).DaysUntil(ProductionDay.On(2026, 3, 29)).ShouldBe(1);
        ProductionDay.On(2026, 3, 29).DaysUntil(ProductionDay.On(2026, 3, 28)).ShouldBe(-1);
        ProductionDay.On(2026, 3, 28).DaysUntil(ProductionDay.On(2026, 3, 28)).ShouldBe(0);
    }

    [Fact]
    public void Comparison_OrdersByDate()
    {
        var earlier = ProductionDay.On(2026, 10, 24);
        var later = ProductionDay.On(2026, 10, 25);

        (earlier < later).ShouldBeTrue();
        (later > earlier).ShouldBeTrue();
        (earlier <= ProductionDay.On(2026, 10, 24)).ShouldBeTrue();
        (later >= ProductionDay.On(2026, 10, 25)).ShouldBeTrue();
    }

    [Fact]
    public void ToString_IsIsoAndCultureIndependent()
    {
        // This string reaches reports, log lines and SQL. A machine running de-DE writing
        // "25.08.2026" would produce a second spelling of the same day, and the two would not join.
        var original = Thread.CurrentThread.CurrentCulture;

        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            ProductionDay.On(2026, 8, 25).ToString().ShouldBe("2026-08-25");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
