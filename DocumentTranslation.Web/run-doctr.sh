#!/bin/bash

# OBSOLETE: This wrapper script is no longer needed.
#
# The DocumentTranslation.Web project has been refactored to use the 
# DocumentTranslationService library directly instead of calling the CLI executable.
#
# This provides better integration, performance, and maintainability.
#
# Configure Azure credentials in the web application's appsettings.json instead.

# Legacy wrapper script - no longer used
# Wrapper script to run doctr without console input issues
# This approach uses nohup and stdbuf to completely detach from console

cd "$(dirname "$0")"/../DocumentTranslation.CLI/bin/Debug/net8.0

# Set environment variables
export DOTNET_CONSOLE="false"
export CI="true"
export TERM="dumb"

# Use stdbuf to disable buffering and nohup to detach from terminal
# Redirect all input from /dev/null and capture output
stdbuf -i0 -o0 -e0 nohup ./doctr "$@" < /dev/null 2>&1
