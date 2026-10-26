using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PccastLibrary.Web.Data;
using PccastLibrary.Web.Services;

namespace PccastLibrary.Web.Pages.Loans;

public class IndexModel(LibraryDbContext db, IPhilippineClock clock) : PageModel
{
    private const int PageSize = 15;
    public static readonly (string Key, string Label)[] Tabs =
    [
        ("active", "Active loans"), ("due", "Due today"), ("overdue", "Overdue"), ("returned", "Returned"), ("all", "All transactions")
    ];

    [BindProperty(SupportsGet = true)] public string? Tab { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;

    public string CurrentTab { get; private set; } = "active";
    public PagedList<Borrower> Loans { get; private set; } = null!;
    public Dictionary<string, int> Counts { get; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        var today = clock.Today;
        CurrentTab = Tabs.Any(t => t.Key == Tab) ? Tab! : "active";

        Counts["active"] = await db.Borrowers.CountAsync(l => l.ReturnedAtUtc == null, ct);
        Counts["due"] = await db.Borrowers.CountAsync(l => l.ReturnedAtUtc == null && l.DueDate == today, ct);
        Counts["overdue"] = await db.Borrowers.CountAsync(l => l.ReturnedAtUtc == null && l.DueDate < today, ct);
        Counts["returned"] = await db.Borrowers.CountAsync(l => l.ReturnedAtUtc != null, ct);
        Counts["all"] = await db.Borrowers.CountAsync(ct);

        IQueryable<Borrower> query = db.Borrowers.AsNoTracking().Include(l => l.Student).Include(l => l.Book);
        query = CurrentTab switch
        {
            "active" => query.Where(l => l.ReturnedAtUtc == null).OrderBy(l => l.DueDate).ThenBy(l => l.Id),
            "due" => query.Where(l => l.ReturnedAtUtc == null && l.DueDate == today).OrderBy(l => l.Id),
            "overdue" => query.Where(l => l.ReturnedAtUtc == null && l.DueDate < today).OrderBy(l => l.DueDate).ThenBy(l => l.Id),
            "returned" => query.Where(l => l.ReturnedAtUtc != null).OrderByDescending(l => l.ReturnedAtUtc).ThenByDescending(l => l.Id),
            _ => query.OrderByDescending(l => l.BorrowedAtUtc).ThenByDescending(l => l.Id)
        };

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var pattern = Q.ToContainsPattern();
            query = query.Where(l =>
                EF.Functions.Like(l.Student.StudentNumber, pattern, "\\") ||
                EF.Functions.Like(l.Student.FirstName + " " + l.Student.LastName, pattern, "\\") ||
                EF.Functions.Like(l.Student.LastName + ", " + l.Student.FirstName, pattern, "\\") ||
                EF.Functions.Like(l.Book.AccessionNumber, pattern, "\\") ||
                EF.Functions.Like(l.Book.Title, pattern, "\\"));
        }

        Loans = await PagedList<Borrower>.CreateAsync(query, PageNumber, PageSize, ct);
    }
}
