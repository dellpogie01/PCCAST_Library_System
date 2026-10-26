using Microsoft.EntityFrameworkCore;
using PccastLibrary.Web.Services;

namespace PccastLibrary.Web.Data;

/// <summary>
/// Optional development dataset. Every record is fictional: student numbers start with "DEMO-"
/// and all e-mail addresses use the reserved example.com domain. Never run against real data.
/// </summary>
public static class DemoDataSeeder
{
    public static async Task<bool> SeedAsync(LibraryDbContext db, IPhilippineClock clock)
    {
        if (await db.Books.AnyAsync() || await db.Students.AnyAsync() || await db.Borrowers.AnyAsync())
            return false;

        string[] firstNames = ["Alden", "Bianca", "Carlo", "Daniela", "Enzo", "Frances", "Gabriel", "Hannah", "Isidro", "Jasmine", "Kristoff", "Lorena"];
        string[] lastNames = ["Alvarez", "Bautista", "Castillo", "Dimaculangan", "Espiritu", "Fajardo", "Gonzales", "Herrera", "Ilagan", "Jacinto", "Lazaro", "Mercado"];
        string[] sections = ["STEM-A", "STEM-B", "ABM-A", "HUMSS-A", "ICT-A", "BSIT-1A"];
        string[] grades = ["Grade 11", "Grade 11", "Grade 12", "Grade 12", "1st Year", "2nd Year"];

        var students = new List<Student>();
        for (var i = 0; i < firstNames.Length; i++)
        {
            students.Add(new Student
            {
                StudentNumber = $"DEMO-2026-{i + 1:000}",
                FirstName = firstNames[i],
                LastName = lastNames[i],
                GradeLevel = grades[i % grades.Length],
                Section = sections[i % sections.Length],
                Email = $"demo.student{i + 1}@example.com",
                ContactNumber = $"0900-000-{1000 + i}",
                IsActive = i != 11 // one inactive student to show the status filter
            });
        }

        (string Title, string Author, string Category, int Copies, int Year)[] titles =
        [
            ("Noli Me Tangere", "José Rizal", "Literature", 3, 1887),
            ("El Filibusterismo", "José Rizal", "Literature", 2, 1891),
            ("Introduction to Algorithms Basics", "Demo Author A", "Computing", 2, 2019),
            ("General Chemistry Essentials", "Demo Author B", "Science", 2, 2018),
            ("Principles of Accounting", "Demo Author C", "Business", 2, 2020),
            ("Philippine History Reader", "Demo Author D", "History", 2, 2017),
            ("Precalculus Workbook", "Demo Author E", "Mathematics", 2, 2021),
            ("Pride and Prejudice", "Jane Austen", "Literature", 1, 1813),
            ("Physics for Senior High", "Demo Author F", "Science", 2, 2019),
            ("Web Development Foundations", "Demo Author G", "Computing", 2, 2022)
        ];
        var books = new List<Book>();
        var n = 1;
        foreach (var t in titles)
            for (var c = 0; c < t.Copies; c++, n++)
                books.Add(new Book
                {
                    AccessionNumber = $"DEMO-{n:0000}",
                    Isbn = $"978-0-00-{100000 + n}-0",
                    Title = t.Title, Author = t.Author, Category = t.Category,
                    Publisher = "Demo Press", PublicationYear = t.Year,
                    ShelfLocation = $"{t.Category[..3].ToUpperInvariant()}-{(n % 6) + 1}"
                });
        books[^1].IsArchived = true; // one retired copy

        db.Students.AddRange(students);
        db.Books.AddRange(books);
        await db.SaveChangesAsync();

        var today = clock.Today;
        const long rate = 500;
        var loans = new List<Borrower>();
        var rng = new Random(2026);

        DateTime At(DateOnly date, int hour) => clock.StartOfLocalDayUtc(date).AddHours(hour);

        // Past months: returned loans, some late with partial / full / no payment.
        for (var i = 0; i < 40; i++)
        {
            var borrowed = today.AddDays(-rng.Next(10, 330));
            var due = borrowed.AddDays(7);
            var late = rng.Next(0, 4) == 0 ? rng.Next(1, 9) : 0;
            var returned = due.AddDays(late - rng.Next(0, 3) * (late == 0 ? 1 : 0));
            if (returned < borrowed) returned = borrowed;
            var assessed = FineCalculator.FineCentavos(due, returned, rate);
            var paid = assessed == 0 ? 0 : (i % 3) switch { 0 => assessed, 1 => assessed / 2 / 100 * 100, _ => 0 };
            loans.Add(new Borrower
            {
                Student = students[rng.Next(0, 11)],
                Book = books[rng.Next(0, books.Count - 1)],
                BorrowedAtUtc = At(borrowed, 9),
                DueDate = due,
                ReturnedAtUtc = At(returned, 15),
                DailyFineRateCentavos = rate,
                AssessedFineCentavos = assessed,
                PaidFineCentavos = paid
            });
        }

        // Current loans: overdue, due today, and not yet due (distinct copies and students).
        var current = new (int Book, int Student, int DueOffset)[]
        {
            (0, 0, -9), (3, 1, -4), (6, 2, -1), (9, 3, 0), (12, 4, 0), (15, 5, 3), (18, 6, 5), (1, 7, 6)
        };
        foreach (var (book, student, offset) in current)
        {
            var due = today.AddDays(offset);
            loans.Add(new Borrower
            {
                Student = students[student],
                Book = books[book],
                BorrowedAtUtc = At(due.AddDays(-7), 10),
                DueDate = due,
                DailyFineRateCentavos = rate
            });
        }

        // The filtered unique index forbids two active loans per copy and the seed copies above
        // are distinct. Past returned loans may share copies freely.
        db.Borrowers.AddRange(loans);
        await db.SaveChangesAsync();
        return true;
    }
}
