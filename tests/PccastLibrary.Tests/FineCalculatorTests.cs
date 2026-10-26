using PccastLibrary.Web.Data;
using PccastLibrary.Web.Services;

namespace PccastLibrary.Tests;

public class FineCalculatorTests
{
    private static readonly DateOnly Due = new(2026, 10, 10);

    [Fact] public void ReturnedOnDueDate_NoFine() =>
        Assert.Equal(0, FineCalculator.FineCentavos(Due, new DateOnly(2026, 10, 10), 500));

    [Fact] public void ReturnedNextDay_OneDayFine() =>
        Assert.Equal(500, FineCalculator.FineCentavos(Due, new DateOnly(2026, 10, 11), 500));

    [Fact] public void StillUnreturnedThreeDaysLate_EstimateIsFifteenPesos() =>
        Assert.Equal(1500, FineCalculator.FineCentavos(Due, new DateOnly(2026, 10, 13), 500));

    [Fact] public void ReturnedEarly_NeverNegative() =>
        Assert.Equal(0, FineCalculator.FineCentavos(Due, new DateOnly(2026, 10, 1), 500));

    [Fact] public void WeekendsAndHolidaysCount() =>
        // Oct 10 2026 is a Saturday; Oct 12 is a Monday: two calendar days overdue.
        Assert.Equal(1000, FineCalculator.FineCentavos(Due, new DateOnly(2026, 10, 12), 500));

    [Fact] public void NoCap_LongOverdueKeepsAccruing() =>
        Assert.Equal(500 * 365, FineCalculator.FineCentavos(Due, Due.AddDays(365), 500));

    [Theory]
    [InlineData("2026-10-10T15:59:59Z", 0)]   // 23:59:59 in Manila, still the due date
    [InlineData("2026-10-10T16:00:00Z", 1)]   // exactly Manila midnight: first overdue day
    [InlineData("2026-10-11T15:59:59Z", 1)]
    [InlineData("2026-10-11T16:00:00Z", 2)]
    public void PhilippineMidnightBoundary_UsesManilaDateNotUtcDate(string utc, int expectedDays)
    {
        var clock = new PhilippineClock(new ManualTimeProvider(DateTimeOffset.Parse(utc)));
        var loan = new Borrower { DueDate = Due, DailyFineRateCentavos = 500 };
        var fine = new FineService(clock).Assess(loan);
        Assert.Equal(expectedDays, fine.OverdueDays);
        Assert.Equal(expectedDays * 500L, fine.AmountCentavos);
        Assert.False(fine.IsFinal);
    }

    [Fact]
    public void ReturnedLoan_FineStopsAccruing()
    {
        // Returned Oct 11 Manila; "now" is much later but the fine must not grow.
        var clock = new PhilippineClock(new ManualTimeProvider(DateTimeOffset.Parse("2026-12-01T00:00:00Z")));
        var loan = new Borrower
        {
            DueDate = Due, DailyFineRateCentavos = 500,
            ReturnedAtUtc = DateTime.Parse("2026-10-11T03:00:00Z").ToUniversalTime(), AssessedFineCentavos = 500, PaidFineCentavos = 200
        };
        var fine = new FineService(clock).Assess(loan);
        Assert.True(fine.IsFinal);
        Assert.Equal(500, fine.AmountCentavos);
        Assert.Equal(300, fine.BalanceCentavos);
    }

    [Fact]
    public void Money_FormatsAsPesos()
    {
        Assert.Equal("₱10.00", Money.Format(1000));
        Assert.Equal("₱0.00", Money.Format(0));
        Assert.Equal("₱1,234.50", Money.Format(123450));
    }

    [Theory]
    [InlineData("10", 1000)]
    [InlineData("10.5", 1050)]
    [InlineData("₱1,000.25", 100025)]
    [InlineData("0.01", 1)]
    public void Money_ParsesValidAmounts(string text, long expected)
    {
        Assert.True(Money.TryParsePesos(text, out var centavos));
        Assert.Equal(expected, centavos);
    }

    [Theory]
    [InlineData("")] [InlineData("abc")] [InlineData("1.234")] [InlineData("1e3")] [InlineData("99999999999")]
    public void Money_RejectsInvalidAmounts(string text) => Assert.False(Money.TryParsePesos(text, out _));
}
