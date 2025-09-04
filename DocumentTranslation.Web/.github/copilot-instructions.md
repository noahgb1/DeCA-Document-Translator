# Copilot Instructions

<!-- Use this file to provide workspace-specific custom instructions to Copilot. For more details, visit https://code.visualstudio.com/docs/copilot/copilot-customization#_use-a-githubcopilotinstructionsmd-file -->

This is an ASP.NET Core web application that provides a web UI for document translation using the DocumentTranslation.CLI command line tool.

## Key Components:
- File upload functionality for documents
- Language selection for translation
- Integration with DocumentTranslation.CLI executable
- Progress tracking and result display
- Modern Bootstrap UI with responsive design

## Architecture:
- Uses Razor Pages for the UI
- Integrates with the existing DocumentTranslation.CLI located in ../DocumentTranslation.CLI/bin/Debug/net8.0/
- Handles file uploads, processes translations, and displays results
- Uses background services for long-running translation tasks
