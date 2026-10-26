using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PccastLibrary.Web.Data;

namespace PccastLibrary.Web.Pages.Students;

public class StudentInput
{
    [Required, StringLength(30), Display(Name = "Student number")]
    public string StudentNumber { get; set; } = "";
    [Required, StringLength(100), Display(Name = "First name")]
    public string FirstName { get; set; } = "";
    [Required, StringLength(100), Display(Name = "Last name")]
    public string LastName { get; set; } = "";
    [Required, StringLength(50), Display(Name = "Grade / year level")]
    public string GradeLevel { get; set; } = "";
    [Required, StringLength(100), Display(Name = "Section / course")]
    public string Section { get; set; } = "";
    [StringLength(200), EmailAddress] public string? Email { get; set; }
    [StringLength(30), Display(Name = "Contact number")]
    [RegularExpression(@"^[0-9+()\- ]*$", ErrorMessage = "Contact number may contain only digits, spaces, + ( ) and hyphens.")]
    public string? ContactNumber { get; set; }
}

/// <summary>Serves both "Add student" (no id) and "Edit student" (with id).</summary>
public class EditModel(LibraryDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty] public StudentInput Input { get; set; } = new();
    public bool IsNew => Id is null;
    public IReadOnlyList<string> Grades { get; private set; } = [];
    public IReadOnlyList<string> Sections { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadListsAsync(ct);
        if (Id is null) return Page();

        var s = await db.Students.AsNoTracking().FirstOrDefaultAsync(x => x.Id == Id, ct);
        if (s is null) return NotFound();
        Input = new StudentInput
        {
            StudentNumber = s.StudentNumber, FirstName = s.FirstName, LastName = s.LastName,
            GradeLevel = s.GradeLevel, Section = s.Section, Email = s.Email, ContactNumber = s.ContactNumber
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await LoadListsAsync(ct);
        Input.StudentNumber = Input.StudentNumber?.Trim() ?? "";
        Input.FirstName = Input.FirstName?.Trim() ?? "";
        Input.LastName = Input.LastName?.Trim() ?? "";
        Input.GradeLevel = Input.GradeLevel?.Trim() ?? "";
        Input.Section = Input.Section?.Trim() ?? "";
        Input.Email = string.IsNullOrWhiteSpace(Input.Email) ? null : Input.Email.Trim();
        Input.ContactNumber = string.IsNullOrWhiteSpace(Input.ContactNumber) ? null : Input.ContactNumber.Trim();

        if (ModelState.IsValid &&
            await db.Students.AnyAsync(s => s.StudentNumber == Input.StudentNumber && s.Id != Id, ct))
            ModelState.AddModelError("Input.StudentNumber", "This student number is already registered.");
        if (!ModelState.IsValid) return Page();

        Student student;
        if (Id is null)
        {
            student = new Student { IsActive = true };
            db.Students.Add(student);
        }
        else
        {
            var existing = await db.Students.FirstOrDefaultAsync(s => s.Id == Id, ct);
            if (existing is null) return NotFound();
            student = existing;
        }

        student.StudentNumber = Input.StudentNumber;
        student.FirstName = Input.FirstName;
        student.LastName = Input.LastName;
        student.GradeLevel = Input.GradeLevel;
        student.Section = Input.Section;
        student.Email = Input.Email;
        student.ContactNumber = Input.ContactNumber;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            ModelState.AddModelError("Input.StudentNumber", "This student number is already registered.");
            return Page();
        }

        TempData["Success"] = IsNew ? "The student was added." : "The student was updated.";
        return RedirectToPage("/Students/Details", new { id = student.Id });
    }

    private async Task LoadListsAsync(CancellationToken ct)
    {
        Grades = await db.Students.Select(s => s.GradeLevel).Distinct().OrderBy(g => g).ToListAsync(ct);
        Sections = await db.Students.Select(s => s.Section).Distinct().OrderBy(g => g).ToListAsync(ct);
    }
}
