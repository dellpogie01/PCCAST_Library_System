using System.Globalization;
using PccastLibrary.Web.Data;

namespace PccastLibrary.Web.Services;

/// <summary>
/// Pure fine arithmetic. Rules: no fine on the due date; overdue days start on the next
/// Philippine calendar date; every calendar day counts; no grace period and no cap.
/// </summary>
public static class FineCalculator
{
    public static int OverdueDays(DateOnly dueDate, DateOnly asOfDate) =>
        Math.Max(0, asOfDate.DayNumber - dueDate.DayNumber);

    public static long FineCentavos(DateOnly dueDate, DateOnly asOfDate, long dailyRateCentavos) =>
        OverdueDays(dueDate, asOfDate) * dailyRateCentavos;
}

/// <summary>Fine position of one loan. <see cref="IsFinal"/> is false for estimates on unreturned loans.</summary>
public sealed record LoanFine(int OverdueDays, long AmountCentavos, bool IsFinal, long PaidCentavos)
{
    /// <summary>Unpaid part of a finalized fine; zero for estimates (nothing can be paid yet).</summary>
    public long BalanceCentavos => IsFinal ? AmountCentavos - PaidCentavos : 0;
}

/// <summary>The single place where loan fines are computed (dashboard, loan details, returns, lists).</summary>
public sealed class FineService(IPhilippineClock clock)
{
    /// <summary>Fine as of the given moment; unreturned loans accrue up to the Philippine "today".</summary>
    public LoanFine Assess(Borrower loan) => Assess(loan, clock.UtcNow);

    public LoanFine Assess(Borrower loan, DateTime asOfUtc)
    {
        if (loan.ReturnedAtUtc is { } returnedAt)
        {
            // Accrual stops at the return date; the stored value is authoritative once finalized.
            var days = FineCalculator.OverdueDays(loan.DueDate, clock.ToLocalDate(returnedAt));
            var amount = loan.AssessedFineCentavos ?? days * loan.DailyFineRateCentavos;
            return new LoanFine(days, amount, true, loan.PaidFineCentavos);
        }

        var asOf = clock.ToLocalDate(asOfUtc);
        return new LoanFine(
            FineCalculator.OverdueDays(loan.DueDate, asOf),
            FineCalculator.FineCentavos(loan.DueDate, asOf, loan.DailyFineRateCentavos),
            false, 0);
    }
}

public static class Money
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Formats centavos as Philippine pesos, e.g. 1000 → ₱10.00.</summary>
    public static string Format(long centavos) =>
        (centavos < 0 ? "-₱" : "₱") + (Math.Abs(centavos) / 100m).ToString("N2", Invariant);

    /// <summary>Parses a peso amount ("10", "10.5", "₱1,000.25") into centavos; rejects more than two decimals.</summary>
    public static bool TryParsePesos(string? text, out long centavos)
    {
        centavos = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var cleaned = text.Trim().TrimStart('₱').Replace(",", "").Trim();
        if (!decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint, Invariant, out var pesos)) return false;
        if (pesos != decimal.Round(pesos, 2) || pesos > 10_000_000m) return false;
        centavos = (long)(pesos * 100m);
        return true;
    }
}
