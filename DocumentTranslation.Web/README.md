# DocumentTranslation.Web

A web application for translating documents using Azure Translator Service.

## Overview

The web application has been **refactored** to use the DocumentTranslationService library directly instead of calling the CLI executable. This provides better performance, error handling, and maintainability.

## Configuration

### Required Settings

Update the `DocumentTranslation` section in `appsettings.json` or `appsettings.Development.json` with your Azure credentials:

```json
{
  "DocumentTranslation": {
    "AzureResourceName": "your-translator-resource-name",
    "SubscriptionKey": "your-translator-subscription-key", 
    "AzureRegion": "your-azure-region",
    "TextTransEndpoint": "https://api.cognitive.microsofttranslator.com/",
    "ShowExperimental": false,
    "Category": "",
    "ConnectionStrings": {
      "StorageConnectionString": "your-azure-storage-connection-string"
    }
  }
}
```

### Azure Resources Required

1. **Azure Translator Service**: For document translation
   - Get the resource name and subscription key from the Azure portal
   - Note the region where your translator resource is deployed

2. **Azure Storage Account**: For temporary file storage during translation
   - Get the connection string from the Azure portal

## Features

- **File Upload**: Support for multiple document formats (Word, PDF, PowerPoint, Excel, Text, HTML, Markdown)
- **Language Detection**: Automatic source language detection or manual selection
- **Multiple Target Languages**: Translate to any supported Azure Translator language
- **Direct Integration**: Uses DocumentTranslationService library directly (no CLI dependency)
- **File Download**: Download individual translated files or all files as a ZIP archive
- **Dynamic Language Loading**: Languages are loaded from Azure Translator Service

## Supported File Formats

- Microsoft Word (.docx)
- PDF Documents (.pdf)
- PowerPoint (.pptx)
- Excel (.xlsx)
- Text Files (.txt)
- HTML Files (.html)
- Markdown (.md)

## Running the Application

```bash
# Build the application
dotnet build

# Run the application
dotnet run
```

The web application will be available at `https://localhost:5001` (or the port specified in your launch settings).

## Architecture Changes

### Before (CLI Integration)
- Web app called DocumentTranslation.CLI via Python wrapper scripts
- Process invocation overhead and complexity
- Limited error handling and logging
- File format handling in CLI

### After (Direct Library Integration)
- Web app references DocumentTranslationService project directly
- Uses dependency injection to manage translation services
- Handles file upload/download within the web app
- Better error handling and logging
- No external process dependencies

### Benefits of Refactoring
- **Performance**: Eliminates process invocation overhead
- **Robustness**: Better error handling and resource management
- **Maintainability**: Type-safe integration, easier debugging
- **Scalability**: Better suited for web deployment scenarios
- **Security**: No external script dependencies

## Development

### Project Structure

```
DocumentTranslation.Web/
├── Models/
│   └── DocumentTranslationOptions.cs   # Configuration model
├── Pages/
│   ├── Shared/
│   │   └── _Layout.cshtml              # Main layout
│   ├── Index.cshtml                    # Home page
│   ├── Translate.cshtml                # Translation page UI
│   ├── Translate.cshtml.cs             # Translation logic (REFACTORED)
│   └── Privacy.cshtml                  # Privacy page
├── wwwroot/
│   └── uploads/                        # Uploaded files directory
├── Program.cs                          # DI configuration (UPDATED)
├── appsettings.json                    # Configuration (UPDATED)
└── DocumentTranslation.Web.csproj     # Project references (UPDATED)
```

### Key Changes Made

1. **DocumentTranslation.Web.csproj**: Added project reference to DocumentTranslationService
2. **Program.cs**: Added DI registration for translation services
3. **Translate.cshtml.cs**: Complete refactor to use service library instead of CLI
4. **appsettings.json**: Added Azure configuration section
5. **Models/DocumentTranslationOptions.cs**: Configuration model for settings

## Security Considerations

- Files are temporarily stored in `wwwroot/uploads/` during processing
- Consider implementing file cleanup routines for production use
- Validate file types and sizes on both client and server side
- Consider adding authentication for production deployments
- Azure credentials should be stored securely (use Azure Key Vault in production)

## Troubleshooting

### Common Issues

1. **Configuration errors**: Ensure Azure credentials are properly set in appsettings
2. **Service initialization**: Check Azure Translator and Storage account connectivity
3. **File upload errors**: Check file size limits and format compatibility
4. **Translation failures**: Verify Azure credentials and network connectivity

### Migration from CLI

If you previously used the CLI-based version:
1. Remove any CLI configuration files or wrapper scripts
2. Update your Azure credentials in the web app configuration
3. The wrapper scripts (`run-doctr.py`, `run-doctr.sh`) are no longer needed

## License

This project is part of the DocumentTranslation solution and follows the same licensing terms.