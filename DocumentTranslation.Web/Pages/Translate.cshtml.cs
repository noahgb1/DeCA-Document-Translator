
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.IO.Compression;
using DocumentTranslationService.Core;

namespace DocumentTranslation.Web.Pages
{
    public class TranslateModel : PageModel
    {
        private readonly ILogger<TranslateModel> _logger;
        private readonly IWebHostEnvironment _environment;
        private readonly DocumentTranslationService.Core.DocumentTranslationService _translationService;
        private readonly DocumentTranslationBusiness _translationBusiness;

        public TranslateModel(
            ILogger<TranslateModel> logger,
            IWebHostEnvironment environment,
            DocumentTranslationService.Core.DocumentTranslationService translationService,
            DocumentTranslationBusiness translationBusiness)
        {
            _logger = logger;
            _environment = environment;
            _translationService = translationService;
            _translationBusiness = translationBusiness;
        }

        // CHANGED: support multiple files
        [BindProperty]
        public List<IFormFile?> UploadedFiles { get; set; } = new();

        [BindProperty]
        public string TargetLanguage { get; set; } = "fr";

        [BindProperty]
        public string SourceLanguage { get; set; } = "";

        public string? Message { get; set; }
        public string? ErrorMessage { get; set; }
        public bool IsTranslating { get; set; }
        public List<string> TranslatedFiles { get; set; } = new();

        public List<LanguageOption> AvailableLanguages { get; set; } = new();

        public async Task OnGetAsync()
        {
            await LoadAvailableLanguagesAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            await LoadAvailableLanguagesAsync();

            // Validate files
            if (UploadedFiles == null || UploadedFiles.Count == 0 || !UploadedFiles.Any(f => f is not null && f.Length > 0))
            {
                ErrorMessage = "Please select at least one file to upload.";
                return Page();
            }

            if (string.IsNullOrEmpty(TargetLanguage))
            {
                ErrorMessage = "Please select a target language.";
                return Page();
            }

            try
            {
                // Initialize the translation service
                await _translationService.InitializeAsync();

                // Create uploads directory
                var uploadsPath = Path.Combine(_environment.WebRootPath, "uploads");
                if (!Directory.Exists(uploadsPath))
                {
                    Directory.CreateDirectory(uploadsPath);
                }

                // Create/clean output directory
                var outputPath = Path.Combine(uploadsPath, "output");
                if (Directory.Exists(outputPath))
                {
                    Directory.Delete(outputPath, true);
                }
                Directory.CreateDirectory(outputPath);

                // Save uploaded files (flattened). Optionally filter extensions server-side.
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".docx",".pdf",".pptx",".xlsx",".txt",".html",".htm",".md"
                };

                var filesToTranslate = new List<string>();
                foreach (var file in UploadedFiles.Where(f => f is not null && f.Length > 0)!)
                {
                    var ext = Path.GetExtension(file!.FileName);
                    if (!allowed.Contains(ext))
                    {
                        _logger.LogWarning("Skipping unsupported file: {File}", file.FileName);
                        continue;
                    }

                    var safeName = Path.GetFileName(file.FileName);
                    var filePath = Path.Combine(uploadsPath, safeName);

                    using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream);
                    }

                    filesToTranslate.Add(filePath);
                }

                if (filesToTranslate.Count == 0)
                {
                    ErrorMessage = "No supported files were selected.";
                    return Page();
                }

                // Setup event handlers for translation progress
                _translationBusiness.OnStatusUpdate += (sender, status) =>
                {
                    _logger.LogInformation($"Translation status: {status}");
                };

                _translationBusiness.OnThereWereErrors += (sender, errors) =>
                {
                    _logger.LogError($"Translation errors: {errors}");
                };

                var targetLanguages = new string[] { TargetLanguage };
                var fromLanguage = string.IsNullOrEmpty(SourceLanguage) ? null : SourceLanguage;

                // Run translation using the service library for all files
                await _translationBusiness.RunAsync(
                    filestotranslate: filesToTranslate,
                    fromlanguage: fromLanguage,
                    tolanguages: targetLanguages,
                    glossaryfiles: null,
                    targetFolder: outputPath);

                Message = $"Translation completed successfully! {filesToTranslate.Count} file(s) translated to {TargetLanguage}.";

                // Get list of translated files directly from output folder
                if (Directory.Exists(outputPath))
                {
                    var files = Directory.GetFiles(outputPath);
                    TranslatedFiles = files.Select(f => Path.GetFileName(f)).ToList();
                    _logger.LogInformation("Translated files: {Files}", string.Join(", ", TranslatedFiles));
                }
                else
                {
                    _logger.LogWarning("Output folder {OutputPath} does not exist", outputPath);
                }
            }
            catch (DocumentTranslationService.Core.DocumentTranslationService.CredentialsException ex)
            {
                _logger.LogError(ex, "Credentials error during translation");
                ErrorMessage = $"Configuration error: {ex.Message}. Please check your Azure Translation Service credentials.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during translation");
                ErrorMessage = $"An error occurred: {ex.Message}";
            }

            return Page();
        }

        private async Task LoadAvailableLanguagesAsync()
        {
            try
            {
                await _translationService.InitializeAsync();

                AvailableLanguages.Clear();
                foreach (var lang in _translationService.Languages.Values)
                {
                    AvailableLanguages.Add(new LanguageOption(lang.LangCode, lang.Name ?? lang.LangCode));
                }

                // Sort by name for better UX
                AvailableLanguages = AvailableLanguages.OrderBy(l => l.Name).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load available languages, using default list");

                // Fallback to a default list if service is not configured
                AvailableLanguages = new List<LanguageOption>
                {
                    new("fr", "French"),
                    new("es", "Spanish"),
                    new("de", "German"),
                    new("it", "Italian"),
                    new("pt", "Portuguese"),
                    new("zh", "Chinese (Simplified)"),
                    new("ja", "Japanese"),
                    new("ko", "Korean"),
                    new("ru", "Russian"),
                    new("ar", "Arabic")
                };
            }
        }

        public async Task<IActionResult> OnGetDownloadAsync(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return NotFound();
            }

            var outputPath = Path.Combine(_environment.WebRootPath, "uploads", "output");
            var filePath = Path.Combine(outputPath, fileName);

            // Security check - ensure the file is within the output directory
            var fullPath = Path.GetFullPath(filePath);
            var fullOutputPath = Path.GetFullPath(outputPath);

            if (!fullPath.StartsWith(fullOutputPath))
            {
                return NotFound();
            }

            if (!System.IO.File.Exists(filePath))
            {
                return NotFound();
            }

            var memory = new MemoryStream();
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                await stream.CopyToAsync(memory);
            }
            memory.Position = 0;

            var contentType = GetContentType(fileName);
            return File(memory, contentType, fileName);
        }

        public async Task<IActionResult> OnGetDownloadAllAsync()
        {
            var outputPath = Path.Combine(_environment.WebRootPath, "uploads", "output");

            if (!Directory.Exists(outputPath))
            {
                return NotFound();
            }

            var files = Directory.GetFiles(outputPath);
            if (files.Length == 0)
            {
                return NotFound();
            }

            // If there's only one file, download it directly
            if (files.Length == 1)
            {
                var fileName = Path.GetFileName(files[0]);
                return await OnGetDownloadAsync(fileName);
            }

            // If multiple files, create a zip archive
            var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
            {
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    var entry = archive.CreateEntry(fileName);

                    using var entryStream = entry.Open();
                    using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read);
                    await fileStream.CopyToAsync(entryStream);
                }
            }

            zipStream.Position = 0;
            return File(zipStream, "application/zip", "translated_files.zip");
        }

        private string GetContentType(string fileName)
        {
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            return extension switch
            {
                ".pdf" => "application/pdf",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".doc" => "application/msword",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                ".ppt" => "application/vnd.ms-powerpoint",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".xls" => "application/vnd.ms-excel",
                ".txt" => "text/plain",
                ".html" => "text/html",
                ".htm" => "text/html",
                ".md" => "text/markdown",
                _ => "application/octet-stream"
            };
        }

        private async Task CopyFileWithRetryAsync(string sourceFile, string destinationFile, int maxRetries = 5)
        {
            for (int retry = 0; retry < maxRetries; retry++)
            {
                try
                {
                    // Try using FileStream for better control over file access
                    await CopyFileWithStreamAsync(sourceFile, destinationFile);
                    return; // Success, exit the retry loop
                }
                catch (IOException ex)
                {
                    bool isRetryableError = ex.Message.Contains("being used by another process") ||
                                            ex.Message.Contains("cannot access the file") ||
                                            ex.HResult == -2147024864 || // ERROR_SHARING_VIOLATION
                                            ex.HResult == -2147024891;   // ERROR_ACCESS_DENIED

                    if (!isRetryableError || retry == maxRetries - 1)
                    {
                        _logger.LogError("Failed to copy file {Source} after {Retries} retries: {Message}",
                            sourceFile, maxRetries, ex.Message);
                        throw;
                    }

                    _logger.LogWarning("File {Source} is locked (attempt {Attempt}/{Max}), retrying.",
                        sourceFile, retry + 1, maxRetries);
                    await Task.Delay((retry + 1) * 1000);
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (retry == maxRetries - 1)
                    {
                        _logger.LogError("Failed to copy file {Source} after {Retries} retries due to access denied: {Message}",
                            sourceFile, maxRetries, ex.Message);
                        throw;
                    }

                    _logger.LogWarning("File {Source} access denied (attempt {Attempt}/{Max}), retrying.",
                        sourceFile, retry + 1, maxRetries);
                    await Task.Delay((retry + 1) * 1000);
                }
            }
        }

        private async Task CopyFileWithStreamAsync(string sourceFile, string destinationFile)
        {
            const int bufferSize = 1024 * 1024; // 1MB buffer

            using var sourceStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan);
            using var destinationStream = new FileStream(destinationFile, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.SequentialScan);

            await sourceStream.CopyToAsync(destinationStream);
        }
    }

    public record LanguageOption(string Code, string Name);
}
