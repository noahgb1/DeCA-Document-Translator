using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.IIS;
using Microsoft.AspNetCore.HttpOverrides;
using DocumentTranslation.Web.Hubs;
using DocumentTranslation.Web.Models;
using DocumentTranslationService.Core;

var builder = WebApplication.CreateBuilder(args);

// Ensure binding to expected port (8080 from WEBSITES_PORT)
var appServicePort = Environment.GetEnvironmentVariable("WEBSITES_PORT") ?? "8080";
var urlsEnv = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrWhiteSpace(urlsEnv))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{appServicePort}");
    Console.WriteLine($"[STARTUP] Binding to http://0.0.0.0:{appServicePort}");
}
else
{
    Console.WriteLine($"[STARTUP] Using existing ASPNETCORE_URLS={urlsEnv}");
}

// Core services
builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddSignalR();

// Translation options
builder.Services.Configure<DocumentTranslationOptions>(
    builder.Configuration.GetSection("DocumentTranslation"));

// Translation service registration
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

// Authentication (cookie + basic OIDC)
// Ensure you have app.UseForwardedHeaders(...) before UseAuthentication() in the pipeline.
var azureAdSection = builder.Configuration.GetSection("AzureAd");
var instance  = azureAdSection["Instance"]?.TrimEnd('/') ?? "https://login.microsoftonline.us";
var tenantId  = azureAdSection["TenantId"];
var clientId  = azureAdSection["ClientId"];

if (string.IsNullOrWhiteSpace(tenantId))
    throw new InvalidOperationException("AzureAd:TenantId is required.");
if (string.IsNullOrWhiteSpace(clientId))
    throw new InvalidOperationException("AzureAd:ClientId is required.");

var authority       = $"{instance}/{tenantId}/v2.0";
var metadataAddress = $"{authority}/.well-known/openid-configuration";

builder.Services
    .AddAuthentication(o =>
    {
        o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(o =>
    {
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.None; // helps with OIDC cross-site redirects
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromMinutes(60);
    })
    .AddOpenIdConnect(o =>
    {
        // Bind first (to pick up ClientId, ClientSecret, CallbackPath, etc.)
        azureAdSection.Bind(o);

        // Then set computed authority endpoints explicitly
        o.Authority = authority;
        o.MetadataAddress = metadataAddress;

        o.ResponseType = "code";
        o.SaveTokens = true;
        o.RequireHttpsMetadata = true;

        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("profile");

        o.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            RoleClaimType = "roles",
            NameClaimType = "name"
        };

        o.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = ctx =>
            {
                // Fallback: if proxy headers say https but Request.Scheme is http, ensure https redirect_uri
                if (!string.Equals(ctx.Request.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ctx.Request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase))
                {
                    var rp = o.CallbackPath.HasValue ? o.CallbackPath.Value : "/signin-oidc";
                    ctx.ProtocolMessage.RedirectUri = $"https://{ctx.Request.Host}{ctx.Request.PathBase}{rp}";
                    Console.WriteLine("[OIDC] Overriding redirect_uri to HTTPS: " + ctx.ProtocolMessage.RedirectUri);
                }
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = ctx =>
            {
                Console.WriteLine("[OIDC] Authentication failed: " + ctx.Exception?.Message);
                return Task.CompletedTask;
            },
            OnRemoteFailure = ctx =>
            {
                Console.WriteLine("[OIDC] Remote failure: " + ctx.Failure?.Message);
                return Task.CompletedTask;
            },
            OnTokenValidated = ctx =>
            {
                Console.WriteLine("[OIDC] Token validated for: " + (ctx.Principal?.Identity?.Name ?? "(no name)"));
                return Task.CompletedTask;
            }
        };
    });

// Authorization
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    options.AddPolicy("RequireTranslatorAdmin", p => p.RequireRole("Translator.Admin"));
    options.AddPolicy("RequireTranslatorUser", p => p.RequireRole("Translator.User"));
});

// Upload limits
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

// Temporary: show identity model PII to see inner network exception
Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Forwarded headers BEFORE auth so scheme/host are corrected
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto
};
// Clear defaults (we trust Azure front end)
fwd.KnownNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);

// Fallback scheme normalization (in case headers not present)
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto)
        && proto.Count > 0
        && proto[0].Equals("https", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Request.Scheme = "https";
    }
    await next();
});

// Remove explicit HTTPS redirection (Azure already terminates TLS)
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/signin", async ctx =>
{
    await ctx.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme,
        new AuthenticationProperties { RedirectUri = "/" });
});

app.MapGet("/signout", async ctx =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    ctx.Response.Redirect("/");
});

app.MapGet("/health", () => Results.Ok("healthy"));

app.MapRazorPages();
app.MapControllers();
app.MapHub<TranslationProgressHub>("/translationProgressHub");

app.Run();

public partial class Program { }