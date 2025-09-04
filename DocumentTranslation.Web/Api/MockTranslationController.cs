using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using DocumentTranslation.Web.Hubs;

namespace DocumentTranslation.Web.Api
{
    [ApiController]
    [Route("api/[controller]")]
    public class MockTranslationController : ControllerBase
    {
        private readonly ILogger<MockTranslationController> _logger;
        private readonly IWebHostEnvironment _environment;
        private readonly IHubContext<TranslationProgressHub> _hubContext;

        public MockTranslationController(
            ILogger<MockTranslationController> logger,
            IWebHostEnvironment environment,
            IHubContext<TranslationProgressHub> hubContext)
        {
            _logger = logger;
            _environment = environment;
            _hubContext = hubContext;
        }

        [HttpPost("translate")]
        public async Task<IActionResult> MockTranslateAsync()
        {
            var UploadedFile = Request.Form.Files["UploadedFile"];
            var TargetLanguage = Request.Form["TargetLanguage"].ToString();
            var SourceLanguage = Request.Form["SourceLanguage"].ToString();
            var connectionId = Request.Form["connectionId"].ToString();
            if (UploadedFile == null || UploadedFile.Length == 0)
            {
                return BadRequest("No file uploaded");
            }

            if (string.IsNullOrEmpty(TargetLanguage))
            {
                return BadRequest("Target language is required");
            }

            try
            {
                // Simulate translation progress with SignalR updates
                if (!string.IsNullOrEmpty(connectionId))
                {
                    var groupName = $"translation_{connectionId}";

                    // Step 1: Uploading
                    await _hubContext.Clients.Group(groupName)
                        .SendAsync("StatusUpdate", new { 
                            message = "Uploading file...",
                            status = "Uploading"
                        });
                    
                    await Task.Delay(1000); // Simulate upload time

                    // Step 2: Processing
                    await _hubContext.Clients.Group(groupName)
                        .SendAsync("StatusUpdate", new { 
                            message = "Processing document...",
                            status = "Processing"
                        });
                    
                    await Task.Delay(1500);

                    // Step 3: Translating
                    await _hubContext.Clients.Group(groupName)
                        .SendAsync("StatusUpdate", new { 
                            message = "Translating content...",
                            status = "Translating"
                        });
                    
                    await Task.Delay(2000);

                    // Step 4: Finalizing
                    await _hubContext.Clients.Group(groupName)
                        .SendAsync("StatusUpdate", new { 
                            message = "Finalizing translation...",
                            status = "Finalizing"
                        });
                    
                    await Task.Delay(1000);

                    // Create mock translated file
                    var uploadsPath = Path.Combine(_environment.WebRootPath, "uploads");
                    if (!Directory.Exists(uploadsPath))
                    {
                        Directory.CreateDirectory(uploadsPath);
                    }

                    var outputPath = Path.Combine(uploadsPath, "output");
                    if (Directory.Exists(outputPath))
                    {
                        Directory.Delete(outputPath, true);
                    }
                    Directory.CreateDirectory(outputPath);

                    // Create a mock translated file
                    var originalFileName = Path.GetFileNameWithoutExtension(UploadedFile.FileName);
                    var extension = Path.GetExtension(UploadedFile.FileName);
                    var translatedFileName = $"{originalFileName}_translated_to_{TargetLanguage}{extension}";
                    var translatedFilePath = Path.Combine(outputPath, translatedFileName);

                    // Create mock translated content
                    var mockTranslatedContent = "Bonjour le monde!\n\nCeci est un document de test pour la traduction.\n\nNous pouvons tester la traduction de ce fichier texte simple.";
                    await System.IO.File.WriteAllTextAsync(translatedFilePath, mockTranslatedContent);

                    var translatedFiles = new List<string> { translatedFileName };

                    // Step 5: Complete
                    await _hubContext.Clients.Group(groupName)
                        .SendAsync("TranslationComplete", new { 
                            message = "Translation completed successfully!",
                            files = translatedFiles
                        });
                }

                return Ok(new { 
                    success = true, 
                    message = "Translation completed successfully!",
                    files = new[] { $"{Path.GetFileNameWithoutExtension(UploadedFile.FileName)}_translated_to_{TargetLanguage}{Path.GetExtension(UploadedFile.FileName)}" }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during mock translation");
                
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