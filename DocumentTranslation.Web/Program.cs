using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.IIS;
using DocumentTranslation.Web.Models;
using DocumentTranslationService.Core;
using DocumentTranslation.Web.Hubs;

using Microsoft.Identity.Web;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------------
// DATA PROTECTION (fix antiforgery decrypt issues across restarts)
// ------------------------------------------------------------------
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo("/home/data-protection"))
    .SetApplicationName("DeCA-Document-Translator");

// ------------------------------------------------------------------
// RAZOR + MICROSOFT IDENTITY UI
// ------------------------------------------------------------------
builder.Services
    .AddRazorPages()
    .AddMicrosoftIdentityUI();

builder.Services.AddControllers();
builder.Services.AddSignalR();

// ------------------------------------------------------------------
// AUTHENTICATION (single registration to avoid duplicate Cookies scheme)
// Use the helper extension provided by Microsoft.Identity.Web.
// This internally registers the cookie & OpenIdConnect schemes once.
// ------------------------------------------------------------------
var azureAdSection = builder.Configuration.GetSection("AzureAd");
if (azureAdSection.Exists())
{
    // This replaces manual AddAuthentication + AddMicrosoftIdentityWebApp calls.
    builder.Services.AddMicrosoftIdentityWebAppAuthentication(builder.Configuration, "AzureAd");
}

// (OPTIONAL) If you need custom cookie settings, use PostConfigure to avoid re‑adding the scheme.
builder.Services.PostConfigure<CookieAuthenticationOptions>(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme, opts =>
{
    opts.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    opts.SlidingExpiration = true;
    opts.ExpireTimeSpan = TimeSpan.FromMinutes(60);
});

// ------------------------------------------------------------------
// AUTHORIZATION
// Temporarily remove the global fallback during debugging so unauthenticated
// requests (e.g., static assets or first visit) don’t trigger multiple redirects.
// Re‑enable once stable.
// ------------------------------------------------------------------
builder.Services.AddAuthorization(options =>
{
    // Commented out fallback for now:
    // options.FallbackPolicy = new AuthorizationPolicyBuilder()
    //     .RequireAuthenticatedUser()
    //     .Build();

    options.AddPolicy("RequireTranslatorAdmin", p => p.RequireRole("Translator.Admin"));
    options.AddPolicy("RequireTranslatorUser", p => p.RequireRole("Translator.User"));
});

// ------------------------------------------------------------------
// DOCUMENT TRANSLATION OPTIONS
// ------------------------------------------------------------------
builder.Services.Configure<DocumentTranslationOptions>(
    builder.Configuration.GetSection("DocumentTranslation"));

builder.Services.AddSingleton<DocumentTranslationService.Core.DocumentTranslationService>(sp =>
{
    var options = sp.GetRequiredService<
        Microsoft.Extensions.Options.IOptions<DocumentTranslationOptions>>().Value;

    Console.WriteLine($"[DEBUG] AzureResourceName: {options.AzureResourceName}");
    Console.WriteLine($"[DEBUG] SubscriptionKey: {(string.IsNullOrEmpty(options.SubscriptionKey) ? "EMPTY" : "***SET***")}");
    Console.WriteLine($"[DEBUG] AzureRegion: {options.AzureRegion}");
    Console.WriteLine($"[DEBUG] TextTransEndpoint: {options.TextTransEndpoint}");
    Console.WriteLine($"[DEBUG] StorageConnectionString: {(string.IsNullOrEmpty(options.ConnectionStrings.StorageConnectionString) ? "EMPTY" : "***SET***")}");

    return new DocumentTranslationService.Core.DocumentTranslationService(
        options.SubscriptionKey,
        options.AzureResourceName,
        options.ConnectionStrings.StorageConnectionString)
    {
        AzureRegion = options.AzureRegion,
        TextTransUri = options.TextTransEndpoint,
        Category = options.Category,
        ShowExperimental = options.ShowExperimental
    };
});

builder.Services.AddScoped<DocumentTranslationBusiness>(sp =>
{
    var svc = sp.GetRequiredService<DocumentTranslationService.Core.DocumentTranslationService>();
    return new DocumentTranslationBusiness(svc);
});

// ------------------------------------------------------------------
// FILE UPLOAD LIMITS
// ------------------------------------------------------------------
builder.Services.Configure<IISServerOptions>(o =>
{
    o.MaxRequestBodySize = 100 * 1024 * 1024;
});
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 100 * 1024 * 1024;
    o.ValueLengthLimit = int.MaxValue;
    o.MultipartHeadersLengthLimit = int.MaxValue;
});

// ------------------------------------------------------------------
// BUILD APP
// ------------------------------------------------------------------
var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// ------------------------------------------------------------------
// FORWARDED HEADERS (apply options explicitly)
// ------------------------------------------------------------------
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                       ForwardedHeaders.XForwardedProto |
                       ForwardedHeaders.XForwardedHost
};
fwd.KnownNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);

// Consider disabling HTTPS redirection inside container to remove “Failed to determine https port”
// if Azure Front End already terminates TLS. Uncomment only if required:
// app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseRouting();

if (azureAdSection.Exists())
{
    app.UseAuthentication();
    app.UseAuthorization();
}

// OPTIONAL manual endpoints (can remove later)
app.MapGet("/signin", async ctx =>
{
    // Single challenge endpoint for manual sign-in if needed
    await ctx.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme,
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = "/" });
});

app.MapGet("/signout", async ctx =>
{
    await ctx.SignOutAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
    ctx.Response.Redirect("/Account/SignedOut");
});

// Health probe
app.MapGet("/health", () => Results.Ok("healthy"));

// Endpoints
app.MapRazorPages();
app.MapControllers();
app.MapHub<TranslationProgressHub>("/translationProgressHub"); // remove .RequireAuthorization() during debugging if needed

app.Run();

public partial class Program { }