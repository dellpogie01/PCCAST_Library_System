using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PccastLibrary.Web.Data;
using PccastLibrary.Web.Services;

namespace PccastLibrary.Web.Pages.Students;

public class DetailsModel(LibraryDbContext db) : PageModel
{
    public Student Student { get; private set; } = null!;
    public IReadOnlyList<Borrower> ActiveLoans { get; private set; } = [];
    public IReadOnlyList<Borrower> History { get; private set; } = [];
    /// <summary>Finalized, unpaid fines on returned loans.</summary>
    public long OutstandingFinesCentavos { get; private set; }
    /// <summary>Estimates on unreturned loans (still accruing).</summary>
    public long EstimatedFinesCentavos { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, [FromServices] FineService fines, CancellationToken ct)
    {
        var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (student is null) return NotFound();
        Student = student;

        var loans = await db.Borrowers.AsNoTracking().Include(l => l.Book).Include(l => l.Student)
            .Where(l => l.StudentId == id).OrderByDescending(l => l.BorrowedAtUtc).ToListAsync(ct);
        ActiveLoans = loans.Where(l => l.ReturnedAtUtc is null).OrderBy(l => l.DueDate).ToList();
        History = loans;
        OutstandingFinesCentavos = loans.Where(l => l.ReturnedAtUtc is not null).Sum(l => fines.Assess(l).BalanceCentavos);
        EstimatedFinesCentavos = ActiveLoans.Sum(l => fines.Assess(l).AmountCentavos);
        return Page();
    }

    public async Task<IActionResult> OnPostDeactivateAsync(int id, CancellationToken ct) => await SetActiveAsync(id, false, ct);
    public async Task<IActionResult> OnPostActivateAsync(int id, CancellationToken ct) => await SetActiveAsync(id, true, ct);

    private async Task<IActionResult> SetActiveAsync(int id, bool active, CancellationToken ct)
    {
        // Students are never deleted: history must stay intact. Existing loans remain returnable.
        var rows = await db.Students.Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, active).SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow), ct);
        if (rows == 0) return NotFound();
        TempData["Success"] = active ? "The student was reactivated." : "The student was deactivated. Existing loans can still be returned.";
        return RedirectToPage(new { id });
    }
}
