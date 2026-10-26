using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PccastLibrary.Web.Services;

namespace PccastLibrary.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("login")]
public class LoginModel(AdminAuthenticator authenticator, ILogger<LoginModel> logger) : PageModel
{
    // One message for every failure so the form never reveals which part was wrong.
    private const string GenericError = "The username or password is incorrect.";

    [BindProperty] public CredentialsInput Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? ErrorMessage { get; private set; }
    public bool IsConfigured => authenticator.IsConfigured;

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? LocalRedirect(SafeReturnUrl()) : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid || !authenticator.Validate(Input.Username, Input.Password))
        {
            logger.LogWarning("Failed administrator sign-in from {RemoteAddress}", HttpContext.Connection.RemoteIpAddress);
            ErrorMessage = GenericError;
            Input.Password = "";
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, authenticator.DisplayName),
            new(ClaimTypes.Role, "Administrator")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
        logger.LogInformation("Administrator signed in");
        return LocalRedirect(SafeReturnUrl());
    }

    /// <summary>Only same-site relative URLs are honoured, which blocks open redirects.</summary>
    private string SafeReturnUrl() => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/";

    public class CredentialsInput
    {
        [Required, StringLength(100)] public string Username { get; set; } = "";
        [Required, StringLength(200)] public string Password { get; set; } = "";
    }
}
