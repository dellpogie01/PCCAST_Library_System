using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PccastLibrary.Web.Data;
using PccastLibrary.Web.Services;

namespace PccastLibrary.Web.Pages.Students;

public sealed class StudentRow
{
    public required Student Student { get; init; }
    public int ActiveLoans { get; init; }
}

public class IndexModel(LibraryDbContext db) : PageModel
{
    private const int PageSize = 15;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Grade { get; set; }
    [BindProperty(SupportsGet = true)] public string? Section { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public string? Dir { get; set; }
    [BindProperty(SupportsGet = true, Name = "page")] public int PageNumber { get; set; } = 1;

    public PagedList<StudentRow> Students { get; private set; } = null!;
    public IReadOnlyList<string> Grades { get; private set; } = [];
    public IReadOnlyList<string> Sections { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        IQueryable<Student> query = db.Students.AsNoTracking();

        if (Status == "active") query = query.Where(s => s.IsActive);
        else if (Status == "inactive") query = query.Where(s => !s.IsActive);
        if (!string.IsNullOrWhiteSpace(Grade)) query = query.Where(s => s.GradeLevel == Grade);
        if (!string.IsNullOrWhiteSpace(Section)) query = query.Where(s => s.Section == Section);

        if (!string.IsNullOrWhiteSpace(Q))
        {
            var pattern = Q.ToContainsPattern();
            query = query.Where(s =>
                EF.Functions.Like(s.StudentNumber, pattern, "\\") ||
                EF.Functions.Like(s.FirstName, pattern, "\\") ||
                EF.Functions.Like(s.LastName, pattern, "\\") ||
                EF.Functions.Like(s.FirstName + " " + s.LastName, pattern, "\\") ||
                EF.Functions.Like(s.LastName + ", " + s.FirstName, pattern, "\\"));
        }

        var desc = Dir == "desc";
        query = (Sort?.ToLowerInvariant()) switch
        {
            "number" => desc ? query.OrderByDescending(s => s.StudentNumber) : query.OrderBy(s => s.StudentNumber),
            "grade" => desc ? query.OrderByDescending(s => s.GradeLevel).ThenBy(s => s.LastName) : query.OrderBy(s => s.GradeLevel).ThenBy(s => s.LastName),
            "section" => desc ? query.OrderByDescending(s => s.Section).ThenBy(s => s.LastName) : query.OrderBy(s => s.Section).ThenBy(s => s.LastName),
            _ => desc ? query.OrderByDescending(s => s.LastName).ThenByDescending(s => s.FirstName) : query.OrderBy(s => s.LastName).ThenBy(s => s.FirstName)
        };

        Students = await PagedList<StudentRow>.CreateAsync(
            query.Select(s => new StudentRow { Student = s, ActiveLoans = s.Loans.Count(l => l.ReturnedAtUtc == null) }),
            PageNumber, PageSize, ct);
        Grades = await db.Students.Select(s => s.GradeLevel).Distinct().OrderBy(g => g).ToListAsync(ct);
        Sections = await db.Students.Select(s => s.Section).Distinct().OrderBy(g => g).ToListAsync(ct);
    }
}
