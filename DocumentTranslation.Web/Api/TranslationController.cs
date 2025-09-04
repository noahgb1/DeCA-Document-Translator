using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using DocumentTranslation.Web.Hubs;
using DocumentTranslationService.Core;

namespace DocumentTranslation.Web.Api
{
    [ApiController]
    [Route("api/[controller]")]
    public class TranslationController : ControllerBase
    {
        private readonly ILogger<TranslationController> _logger;
        private readonly IWebHostEnvironment _environment;
        private readonly DocumentTranslationService.Core.DocumentTranslationService _translationService;
        private readonly IHubContext<TranslationProgressHub> _hubContext;

        public TranslationController(
            ILogger<TranslationController> logger,
            IWebHostEnvironment environment,
            DocumentTranslationService.Core.DocumentTranslationService translationService,
            IHubContext<TranslationProgressHub> hubContext)
        {
            _logger = logger;
            _environment = environment;
            _translationService = translationService;
            _hubContext = hubContext;
        }

        [HttpPost("translate")]
        public async Task<IActionResult> TranslateAsync(
            [FromForm] IFormFile file,
            [FromForm] string targetLanguage,
            [FromForm] string? sourceLanguage = null,
            [FromForm] string? connectionId = null)
        {
            // Debug logging to see what we're actually receiving
            _logger.LogInformation("TranslateAsync called with:");
            _logger.LogInformation("- file: {FileName} ({FileSize} bytes)", file?.FileName ?? "null", file?.Length ?? 0);
            _logger.LogInformation("- targetLanguage: '{TargetLanguage}'", targetLanguage ?? "null");
            _logger.LogInformation("- sourceLanguage: '{SourceLanguage}'", sourceLanguage ?? "null");
            _logger.LogInformation("- connectionId: '{ConnectionId}'", connectionId ?? "null");
            
            // Also log all form data received
            _logger.LogInformation("All form data received:");
            foreach (var formKey in Request.Form.Keys)
            {
                _logger.LogInformation("- {Key}: '{Value}'", formKey, Request.Form[formKey]);
            }

            if (file == null || file.Length == 0)
            {
                _logger.LogWarning("No file uploaded");
                return BadRequest("No file uploaded");
            }

            if (string.IsNullOrEmpty(targetLanguage))
            {
                _logger.LogWarning("Target language is required but was: '{TargetLanguage}'", targetLanguage ?? "null");
                return BadRequest("Target language is required");
            }

            try
            {
                var translationBusiness = new DocumentTranslationBusiness(_translationService);
                
                // Set up event handlers for real-time progress updates
                if (!string.IsNullOrEmpty(connectionId))
                {
                    translationBusiness.OnStatusUpdate += async (sender, status) =>
                    {
                        _logger.LogInformation("OnStatusUpdate fired - Message: '{Message}', Status: '{Status}'", 
                            status.Message ?? "null", status.Status?.Status.ToString() ?? "null");
                        
                        await _hubContext.Clients.Group($"translation_{connectionId}")
                            .SendAsync("StatusUpdate", new { 
                                message = status.Message ?? "Translating...",
                                status = status.Status?.Status.ToString() ?? "InProgress"
                            });
                    };

                    translationBusiness.OnUploadStart += async (sender, e) =>
                    {
                        _logger.LogInformation("OnUploadStart fired");
                        await _hubContext.Clients.Group($"translation_{connectionId}")
                            .SendAsync("StatusUpdate", new { 
                                message = "Uploading file...",
                                status = "Uploading"
                            });
                    };

                    translationBusiness.OnUploadComplete += async (sender, e) =>
                    {
                        _logger.LogInformation("OnUploadComplete fired");
                        await _hubContext.Clients.Group($"translation_{connectionId}")
                            .SendAsync("StatusUpdate", new { 
                                message = "Upload complete. Starting translation...",
                                status = "Translating"
                            });
                    };

                    translationBusiness.OnDownloadComplete += async (sender, e) =>
                    {
                        _logger.LogInformation("OnDownloadComplete fired");
                        await _hubContext.Clients.Group($"translation_{connectionId}")
                            .SendAsync("StatusUpdate", new { 
                                message = "Translation complete. Preparing download...",
                                status = "Completing"
                            });
                    };

                    translationBusiness.OnThereWereErrors += async (sender, errors) =>
                    {
                        _logger.LogError("OnThereWereErrors fired: {Errors}", errors);
                        await _hubContext.Clients.Group($"translation_{connectionId}")
                            .SendAsync("TranslationError", new { 
                                message = $"Translation error: {errors}",
                                error = errors
                            });
                    };
                }

                // Initialize the translation service
                await _translationService.InitializeAsync();
                
                // Send initial status update
                if (!string.IsNullOrEmpty(connectionId))
                {
                    await _hubContext.Clients.Group($"translation_{connectionId}")
                        .SendAsync("StatusUpdate", new { 
                            message = "Initializing Azure Translation service...",
                            status = "Initializing"
                        });
                }

                // Create uploads directory
                var uploadsPath = Path.Combine(_environment.WebRootPath, "uploads");
                if (!Directory.Exists(uploadsPath))
                {
                    Directory.CreateDirectory(uploadsPath);
                }

                // Save uploaded file
                var fileName = Path.GetFileName(file.FileName);
                var filePath = Path.Combine(uploadsPath, fileName);
                
                // Send upload status
                if (!string.IsNullOrEmpty(connectionId))
                {
                    await _hubContext.Clients.Group($"translation_{connectionId}")
                        .SendAsync("StatusUpdate", new { 
                            message = "Preparing document for translation...",
                            status = "Preparing"
                        });
                }
                
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                // Create output directory
                var outputPath = Path.Combine(uploadsPath, "output");
                if (Directory.Exists(outputPath))
                {
                    Directory.Delete(outputPath, true);
                }
                Directory.CreateDirectory(outputPath);

                // Run translation
                var filesToTranslate = new List<string> { filePath };
                var targetLanguages = new string[] { targetLanguage };
                var fromLanguage = string.IsNullOrEmpty(sourceLanguage) ? null : sourceLanguage;

                _logger.LogInformation("Starting translation with parameters:");
                _logger.LogInformation("- Files to translate: {Files}", string.Join(", ", filesToTranslate));
                _logger.LogInformation("- From language: '{FromLanguage}'", fromLanguage ?? "auto-detect");
                _logger.LogInformation("- To languages: {ToLanguages}", string.Join(", ", targetLanguages));
                _logger.LogInformation("- Output path: {OutputPath}", outputPath);

                // Send translation starting status
                if (!string.IsNullOrEmpty(connectionId))
                {
                    await _hubContext.Clients.Group($"translation_{connectionId}")
                        .SendAsync("StatusUpdate", new { 
                            message = "Starting document translation with Azure AI...",
                            status = "Translating"
                        });
                }

                try
                {
                    await translationBusiness.RunAsync(
                        filestotranslate: filesToTranslate,
                        fromlanguage: fromLanguage,
                        tolanguages: targetLanguages,
                        glossaryfiles: null,
                        targetFolder: outputPath);
                    
                    _logger.LogInformation("Translation completed successfully");
                    
                    // Send completion status before checking files
                    if (!string.IsNullOrEmpty(connectionId))
                    {
                        await _hubContext.Clients.Group($"translation_{connectionId}")
                            .SendAsync("StatusUpdate", new { 
                                message = "Translation complete! Preparing download...",
                                status = "Completed"
                            });
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Translation failed with exception: {Message}", ex.Message);
                    
                    // Send error via SignalR
                    if (!string.IsNullOrEmpty(connectionId))
                    {
                        await _hubContext.Clients.Group($"translation_{connectionId}")
                            .SendAsync("TranslationError", new { 
                                message = $"Translation failed: {ex.Message}",
                                error = ex.ToString()
                            });
                    }
                    
                    throw; // Re-throw to be handled by outer catch
                }

                // Get list of translated files
                var translatedFiles = new List<string>();
                if (Directory.Exists(outputPath))
                {
                    var files = Directory.GetFiles(outputPath);
                    translatedFiles = files.Select(f => Path.GetFileName(f)).ToList();
                    _logger.LogInformation("Found {FileCount} translated files in output directory: {Files}", 
                        translatedFiles.Count, string.Join(", ", translatedFiles));
                }
                else
                {
                    _logger.LogWarning("Output directory does not exist: {OutputPath}", outputPath);
                }

                // Notify completion
                if (!string.IsNullOrEmpty(connectionId))
                {
                    _logger.LogInformation("Sending TranslationComplete notification via SignalR to connection: {ConnectionId}", connectionId);
                    await _hubContext.Clients.Group($"translation_{connectionId}")
                        .SendAsync("TranslationComplete", new { 
                            message = "Translation completed successfully!",
                            files = translatedFiles
                        });
                }
                else
                {
                    _logger.LogWarning("No connectionId provided, cannot send SignalR notification");
                }

                return Ok(new { 
                    success = true, 
                    message = "Translation completed successfully!",
                    files = translatedFiles
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during translation");
                
                if (!string.IsNullOrEmpty(connectionId))
                {
                    await _hubContext.Clients.Group($"translation_{connectionId}")
                        .SendAsync("TranslationError", new { 
                            message = $"Translation failed: {ex.Message}",
                            error = ex.Message
                        });
                }
                
                return StatusCode(500, new { 
                    success = false, 
                    message = $"Translation failed: {ex.Message}" 
                });
            }
        }
    }
}