# Document Translation Web UI

A simple ASP.NET Core web application that provides a user-friendly interface for document translation using the DocumentTranslation.CLI command line tool.

## Features

- **File Upload**: Easy drag-and-drop file upload interface
- **Multiple Languages**: Support for translation to/from multiple languages
- **Auto-Detection**: Automatic source language detection
- **Modern UI**: Responsive Bootstrap-based interface with Font Awesome icons
- **Multiple Formats**: Support for Word, PDF, PowerPoint, Excel, Text, HTML, and Markdown files
- **CLI Integration**: Seamless integration with the existing DocumentTranslation.CLI tool

## Supported File Formats

- Microsoft Word (.docx)
- PDF Documents (.pdf)
- PowerPoint (.pptx)
- Excel (.xlsx)
- Text Files (.txt)
- HTML Files (.html)
- Markdown (.md)

## Prerequisites

1. **.NET 8.0**: Make sure you have .NET 8.0 SDK installed
2. **DocumentTranslation.CLI**: The CLI tool must be built and available at `../DocumentTranslation.CLI/bin/Debug/net8.0/`
3. **Azure Configuration**: The CLI tool must be properly configured with Azure credentials

## Getting Started

### 1. Configure the CLI Tool

First, make sure the DocumentTranslation.CLI is configured with your Azure credentials:

```bash
cd ../DocumentTranslation.CLI/bin/Debug/net8.0
./doctr config set --key "your-azure-key"
./doctr config set --storage "your-storage-connection-string"
./doctr config set --endpoint "https://your-resource.cognitiveservices.azure.com/"
./doctr config set --region "your-region"
./doctr config test
```

### 2. Run the Web Application

```bash
# Build the application
dotnet build

# Run the application
dotnet run
```

The application will start and be available at `https://localhost:5001` (or the URL shown in the console).

### 3. Using the Application

1. Navigate to the **Translate** page
2. Upload your document using the file picker
3. Select the target language for translation
4. Optionally specify the source language (or leave blank for auto-detection)
5. Click **Translate Document**
6. Download the translated document when ready

## Project Structure

```
DocumentTranslation.Web/
├── Pages/
│   ├── Shared/
│   │   └── _Layout.cshtml          # Main layout with navigation
│   ├── Index.cshtml                # Home page
│   ├── Translate.cshtml            # Translation page UI
│   ├── Translate.cshtml.cs         # Translation page logic
│   └── Privacy.cshtml              # Privacy page
├── wwwroot/
│   ├── uploads/                    # Uploaded files directory
│   ├── css/                        # Custom styles
│   └── js/                         # Client-side scripts
├── Program.cs                      # Application configuration
└── DocumentTranslation.Web.csproj # Project file
```

## Configuration

### File Upload Limits

The application is configured to handle files up to 100MB in size. You can modify these limits in `Program.cs`:

```csharp
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 100 * 1024 * 1024; // 100MB
});
```

### CLI Path

The application looks for the CLI tool at `../DocumentTranslation.CLI/bin/Debug/net8.0/doctr`. If your CLI is located elsewhere, update the path in `Translate.cshtml.cs`:

```csharp
var cliPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "DocumentTranslation.CLI", "bin", "Debug", "net8.0", "doctr");
```

## Development

### Adding New Languages

To add support for additional languages, update the `AvailableLanguages` list in `Translate.cshtml.cs`:

```csharp
public List<LanguageOption> AvailableLanguages { get; set; } = new()
{
    new("fr", "French"),
    new("es", "Spanish"),
    // Add more languages here
};
```

### Customizing the UI

- **Styles**: Add custom CSS to `wwwroot/css/site.css`
- **Scripts**: Add custom JavaScript to `wwwroot/js/site.js`
- **Layout**: Modify `Pages/Shared/_Layout.cshtml` for global changes

## Security Considerations

- Files are temporarily stored in `wwwroot/uploads/` during processing
- Consider implementing file cleanup routines for production use
- Validate file types and sizes on both client and server side
- Consider adding authentication for production deployments

## Troubleshooting

### Common Issues

1. **CLI not found**: Ensure the DocumentTranslation.CLI is built and the path is correct
2. **Configuration errors**: Run `doctr config test` to verify CLI configuration
3. **File upload errors**: Check file size limits and format compatibility
4. **Translation failures**: Verify Azure credentials and network connectivity

### Logs

The application logs translation attempts and errors. Check the console output when running in development mode.

## License

This project is part of the DocumentTranslation solution and follows the same licensing terms.
