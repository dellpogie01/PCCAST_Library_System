using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PccastLibrary.Web;
using PccastLibrary.Web.Data;
using PccastLibrary.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.SectionName));
builder.Services.Configure<LibraryOptions>(builder.Configuration.GetSection(LibraryOptions.SectionName));
var libraryOptions = builder.Configuration.GetSection(LibraryOptions.SectionName).Get<LibraryOptions>() ?? new();

// The database lives in App_Data under the content root (not wwwroot), so it is never served.
var databasePath = DatabaseLocation.Resolve(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddDbContext<LibraryDbContext>(o => o.UseSqlite($"Data Source={databasePath}"));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPhilippineClock, PhilippineClock>();
builder.Services.AddSingleton<AdminAuthenticator>();
builder.Services.AddScoped<FineService>();
builder.Services.AddScoped<LoanService>();
builder.Services.AddScoped<DashboardService>();

var requireSecureCookies = builder.Configuration.GetValue("Security:RequireSecureCookies", !builder.Environment.IsDevelopment());
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "PccastLibrary.Auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = requireSecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/Login";
        o.ExpireTimeSpan = TimeSpan.FromMinutes(libraryOptions.SessionMinutes);
        o.SlidingExpiration = true;
    });

// Every endpoint requires a signed-in administrator unless it opts out with [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = "PccastLibrary.Antiforgery";
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = requireSecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});

// Sign-in attempts are limited per client address; GET requests are not counted.
var loginPermits = builder.Configuration.GetValue("Security:LoginAttemptsPer5Minutes", 8);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", context =>
        HttpMethods.IsPost(context.Request.Method)
            ? RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = loginPermits,
                    Window = TimeSpan.FromMinutes(5),
                    QueueLimit = 0
                })
            : RateLimitPartition.GetNoLimiter("read"));
    o.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "300";
        context.HttpContext.Response.ContentType = "text/plain";
        await context.HttpContext.Response.WriteAsync("Too many sign-in attempts. Please wait a few minutes and try again.", ct);
    };
});

builder.Services.AddRazorPages(o =>
{
    // One page serves both "add" and "edit" for each entity.
    o.Conventions.AddPageRoute("/Books/Edit", "Books/Create");
    o.Conventions.AddPageRoute("/Students/Edit", "Students/Create");
});

var useForwardedHeaders = builder.Configuration.GetValue("Security:UseForwardedHeaders", false);
if (useForwardedHeaders)
{
    // Enable only behind a reverse proxy you control, so clients cannot spoof their address.
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
}

var app = builder.Build();

if (args.Length > 0 && Cli.IsCommand(args[0]))
{
    return await Cli.RunAsync(app, args);
}

await DatabaseLocation.MigrateAsync(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
if (useForwardedHeaders) app.UseForwardedHeaders();
if (builder.Configuration.GetValue("Security:HttpsRedirection", true)) app.UseHttpsRedirection();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; " +
        "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "same-origin";
    headers["Cache-Control"] = context.Request.Path.StartsWithSegments("/css") || context.Request.Path.StartsWithSegments("/js")
        ? "no-cache" : "no-store";
    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

await app.RunAsync();
return 0;

/// <summary>Entry point marker so integration tests can host the application.</summary>
public partial class Program;
