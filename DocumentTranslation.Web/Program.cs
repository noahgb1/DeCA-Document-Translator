using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.IIS;
using DocumentTranslation.Web.Models;
using DocumentTranslationService.Core;
using DocumentTranslation.Web.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddSignalR();

// Configure DocumentTranslation settings
builder.Services.Configure<DocumentTranslationOptions>(
    builder.Configuration.GetSection("DocumentTranslation"));

// Register DocumentTranslation services
builder.Services.AddSingleton<DocumentTranslationService.Core.DocumentTranslationService>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DocumentTranslationOptions>>().Value;
    
    // Add debug logging to see what values we're getting
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

builder.Services.AddScoped<DocumentTranslationBusiness>(serviceProvider =>
{
    var translationService = serviceProvider.GetRequiredService<DocumentTranslationService.Core.DocumentTranslationService>();
    return new DocumentTranslationBusiness(translationService);
});

// Configure file upload limits
builder.Services.Configure<IISServerOptions>(options =>
{
    options.MaxRequestBodySize = 100 * 1024 * 1024; // 100MB
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 100 * 1024 * 1024; // 100MB
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();
app.MapHub<TranslationProgressHub>("/translationProgressHub");

app.Run();

// Make Program class accessible for testing
public partial class Program { }
