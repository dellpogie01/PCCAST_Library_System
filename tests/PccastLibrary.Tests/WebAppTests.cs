using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PccastLibrary.Web.Data;
using PccastLibrary.Web.Services;

namespace PccastLibrary.Tests;

/// <summary>Hosts the real application against a temporary database and a throw-away administrator hash.</summary>
public sealed class AppFixture : WebApplicationFactory<Program>
{
    public const string Username = "librarian";
    public const string Password = "Correct-Horse-Battery-9";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pccast-http-{Guid.NewGuid():N}.db");

    public AppFixture()
    {
        // Environment variables are read by the application's configuration before Program runs.
        Environment.SetEnvironmentVariable("Database__Path", _dbPath);
        Environment.SetEnvironmentVariable("Admin__Username", Username);
        Environment.SetEnvironmentVariable("Admin__PasswordHash", AdminAuthenticator.HashPassword(Password));
        Environment.SetEnvironmentVariable("Security__RequireSecureCookies", "false");
        Environment.SetEnvironmentVariable("Security__HttpsRedirection", "false");
        Environment.SetEnvironmentVariable("Security__LoginAttemptsPer5Minutes", "100");
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        foreach (var s in new[] { "", "-wal", "-shm" }) try { File.Delete(_dbPath + s); } catch (IOException) { }
    }
}

public class WebAppTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(m.Success, $"antiforgery token not found on {url}");
        return m.Groups[1].Value;
    }

    private async Task<HttpClient> SignedInClientAsync()
    {
        var client = app.NewClient();
        var token = await TokenAsync(client, "/Account/Login");
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = AppFixture.Username, ["Input.Password"] = AppFixture.Password, ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string formPage, string postUrl, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await TokenAsync(client, formPage);
        return await client.PostAsync(postUrl, new FormUrlEncodedContent(fields));
    }

    [Theory]
    [InlineData("/")] [InlineData("/Books")] [InlineData("/Books/Create")] [InlineData("/Books/Details/1")]
    [InlineData("/Students")] [InlineData("/Students/Create")] [InlineData("/Loans")] [InlineData("/Loans/Borrow")]
    [InlineData("/Loans/Return")] [InlineData("/Loans/Details/1")]
    public async Task ProtectedPages_RedirectAnonymousUsersToLogin(string url)
    {
        var response = await app.NewClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/Login", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task AnonymousPost_IsRejected()
    {
        var response = await app.NewClient().PostAsync("/Books/Create", new FormUrlEncodedContent(new Dictionary<string, string> { ["Input.Title"] = "x" }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/Login", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task LoginPage_IsPublic_BrandedAndSendsSecurityHeaders()
    {
        var response = await app.NewClient().GetAsync("/Account/Login");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("PCCAST", html);
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task WrongCredentials_ShowGenericErrorAndDoNotSignIn()
    {
        var client = app.NewClient();
        foreach (var (user, pass) in new[] { (AppFixture.Username, "wrong-password"), ("nobody", AppFixture.Password) })
        {
            var token = await TokenAsync(client, "/Account/Login");
            var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["Input.Username"] = user, ["Input.Password"] = pass, ["__RequestVerificationToken"] = token }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("The username or password is incorrect.", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Login_MissingAntiforgeryToken_IsRejected()
    {
        var response = await app.NewClient().PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Input.Username"] = AppFixture.Username, ["Input.Password"] = AppFixture.Password }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example/steal", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/Books", "/Books")]
    public async Task Login_OnlyHonoursLocalReturnUrls(string returnUrl, string expected)
    {
        var client = app.NewClient();
        var token = await TokenAsync(client, "/Account/Login");
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = AppFixture.Username, ["Input.Password"] = AppFixture.Password,
            ["ReturnUrl"] = returnUrl, ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location!.OriginalString);
        var cookie = response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("PccastLibrary.Auth"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Logout_EndsTheSession()
    {
        var client = await SignedInClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        var response = await PostFormAsync(client, "/", "/Account/Logout", new());
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task BooksAndStudents_CanBeCreated_AndDuplicatesAreRejected()
    {
        var client = await SignedInClientAsync();
        var bookFields = () => new Dictionary<string, string>
        {
            ["Input.AccessionNumber"] = "WEB-0001", ["Input.Title"] = "Web Test Book", ["Input.Author"] = "A. Writer",
            ["Input.Category"] = "Testing", ["Input.ShelfLocation"] = "T-1"
        };
        Assert.Equal(HttpStatusCode.Redirect, (await PostFormAsync(client, "/Books/Create", "/Books/Create", bookFields())).StatusCode);

        var duplicate = await PostFormAsync(client, "/Books/Create", "/Books/Create", bookFields());
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Contains("already used", await duplicate.Content.ReadAsStringAsync());

        var studentFields = () => new Dictionary<string, string>
        {
            ["Input.StudentNumber"] = "WEB-S-1", ["Input.FirstName"] = "Demo", ["Input.LastName"] = "Student",
            ["Input.GradeLevel"] = "Grade 11", ["Input.Section"] = "A"
        };
        Assert.Equal(HttpStatusCode.Redirect, (await PostFormAsync(client, "/Students/Create", "/Students/Create", studentFields())).StatusCode);
        var dupStudent = await PostFormAsync(client, "/Students/Create", "/Students/Create", studentFields());
        Assert.Equal(HttpStatusCode.OK, dupStudent.StatusCode);
        Assert.Contains("already registered", await dupStudent.Content.ReadAsStringAsync());

        var list = await client.GetStringAsync("/Books?q=WEB-0001");
        Assert.Contains("Web Test Book", list);
        Assert.Contains("Available", list);
    }

    [Fact]
    public async Task BorrowReturnPayAndArchiveRules_WorkEndToEnd()
    {
        var client = await SignedInClientAsync();
        await PostFormAsync(client, "/Books/Create", "/Books/Create", new()
        { ["Input.AccessionNumber"] = "FLOW-1", ["Input.Title"] = "Flow Book", ["Input.Author"] = "A", ["Input.Category"] = "C", ["Input.ShelfLocation"] = "S" });
        await PostFormAsync(client, "/Students/Create", "/Students/Create", new()
        { ["Input.StudentNumber"] = "FLOW-S1", ["Input.FirstName"] = "F", ["Input.LastName"] = "L", ["Input.GradeLevel"] = "G", ["Input.Section"] = "S" });
        await PostFormAsync(client, "/Students/Create", "/Students/Create", new()
        { ["Input.StudentNumber"] = "FLOW-S2", ["Input.FirstName"] = "F2", ["Input.LastName"] = "L2", ["Input.GradeLevel"] = "G", ["Input.Section"] = "S" });

        int bookId, studentId, student2Id;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            bookId = await db.Books.Where(b => b.AccessionNumber == "FLOW-1").Select(b => b.Id).SingleAsync();
            studentId = await db.Students.Where(s => s.StudentNumber == "FLOW-S1").Select(s => s.Id).SingleAsync();
            student2Id = await db.Students.Where(s => s.StudentNumber == "FLOW-S2").Select(s => s.Id).SingleAsync();
        }

        var borrowUrl = $"/Loans/Borrow?studentId={studentId}&bookId={bookId}";
        var borrowed = await PostFormAsync(client, borrowUrl, "/Loans/Borrow", new()
        { ["StudentId"] = studentId.ToString(), ["BookId"] = bookId.ToString(), ["DueDate"] = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd") });
        Assert.Equal(HttpStatusCode.Redirect, borrowed.StatusCode);

        // A second borrower cannot take the same copy; the confirm step is not even offered.
        var page = await client.GetStringAsync($"/Loans/Borrow?studentId={student2Id}&bookId={bookId}");
        Assert.Contains("already borrowed", page);

        // Archive is refused while borrowed.
        var archive = await PostFormAsync(client, "/", $"/Books/Details/{bookId}?handler=Archive", new());
        Assert.Equal(HttpStatusCode.Redirect, archive.StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            Assert.False((await db.Books.SingleAsync(b => b.Id == bookId)).IsArchived);
        }

        // Dashboard renders with live figures.
        var dash = await client.GetStringAsync("/");
        Assert.Contains("Estimated overdue fines", dash);
        Assert.Contains("Outstanding assessed fines", dash);

        int loanId;
        using (var scope = app.Services.CreateScope())
            loanId = await scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Borrowers.Where(l => l.BookId == bookId).Select(l => l.Id).SingleAsync();

        var ret = await PostFormAsync(client, $"/Loans/Return/{loanId}", $"/Loans/Return/{loanId}", new());
        Assert.Equal(HttpStatusCode.Redirect, ret.StatusCode);
        var secondReturn = await PostFormAsync(client, $"/Loans/Details/{loanId}", $"/Loans/Return/{loanId}", new());
        Assert.Equal(HttpStatusCode.Redirect, secondReturn.StatusCode);
        Assert.Contains("already been returned", await client.GetStringAsync($"/Loans/Details/{loanId}"));

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            Assert.Equal(1, await db.Borrowers.CountAsync(l => l.BookId == bookId && l.ReturnedAtUtc != null));
        }
        var archived = await PostFormAsync(client, "/", $"/Books/Details/{bookId}?handler=Archive", new());
        Assert.Equal(HttpStatusCode.Redirect, archived.StatusCode);
        using (var scope = app.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<LibraryDbContext>().Books.SingleAsync(b => b.Id == bookId)).IsArchived);
    }
}
