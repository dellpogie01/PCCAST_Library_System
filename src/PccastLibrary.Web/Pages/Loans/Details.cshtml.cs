using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PccastLibrary.Web.Data;
using PccastLibrary.Web.Services;

namespace PccastLibrary.Web.Pages.Loans;

public class DetailsModel(LibraryDbContext db, LoanService loans) : PageModel
{
    public Borrower Loan { get; private set; } = null!;
    public LoanFine Fine { get; private set; } = null!;

    [BindProperty] public string? AmountPesos { get; set; }
    [BindProperty] public long ExpectedPaidCentavos { get; set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        AmountPesos = Fine.BalanceCentavos > 0 ? (Fine.BalanceCentavos / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : null;
        ExpectedPaidCentavos = Loan.PaidFineCentavos;
        return Page();
    }

    /// <summary>Records a full or partial payment against a finalized fine (cumulative tracker, no receipts).</summary>
    public async Task<IActionResult> OnPostPayAsync(int id, CancellationToken ct)
    {
        if (!Money.TryParsePesos(AmountPesos, out var centavos) || centavos <= 0)
        {
            TempData["Error"] = "Enter a valid payment amount in pesos, greater than zero, with at most two decimal places.";
            return RedirectToPage(new { id });
        }

        var result = await loans.RecordPaymentAsync(id, centavos, ExpectedPaidCentavos, ct);
        if (result.Succeeded) TempData["Success"] = $"Payment of {Money.Format(centavos)} recorded.";
        else TempData["Error"] = result.Error;
        return RedirectToPage(new { id });
    }

    private async Task<bool> LoadAsync(int id, CancellationToken ct)
    {
        var loan = await db.Borrowers.AsNoTracking().Include(l => l.Student).Include(l => l.Book)
            .FirstOrDefaultAsync(l => l.Id == id, ct);
        if (loan is null) return false;
        Loan = loan;
        Fine = loans.Assess(loan);
        return true;
    }
}
