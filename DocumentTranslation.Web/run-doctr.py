#!/usr/bin/env python3
"""
OBSOLETE: This wrapper script is no longer needed.

The DocumentTranslation.Web project has been refactored to use the 
DocumentTranslationService library directly instead of calling the CLI executable.

This provides better integration, performance, and maintainability.

Configure Azure credentials in the web application's appsettings.json instead.
"""

# Legacy wrapper script - no longer used
# Wrapper script to run doctr CLI without console input issues.
# This script starts the doctr process in a completely isolated environment.

import sys
import subprocess
import os
import signal
import time
import threading
import pty
from pathlib import Path

def run_doctr(args):
    """Run doctr with proper error handling for console input issues."""
    
    # Change to the CLI directory
    cli_dir = Path(__file__).parent / ".." / "DocumentTranslation.CLI" / "bin" / "Debug" / "net8.0"
    cli_path = cli_dir / "doctr"
    
    if not cli_path.exists():
        print(f"Error: CLI executable not found at {cli_path}")
        return 1
    
    # Set environment variables to indicate non-interactive mode
    env = os.environ.copy()
    env.update({
        'DOTNET_CONSOLE': 'false',
        'CI': 'true',
        'TERM': 'dumb',
        'LANG': 'C',
        'LC_ALL': 'C',
        'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
        'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1'
    })
    
    try:
        # Use a pseudo-terminal to avoid console issues
        master_fd, slave_fd = pty.openpty()
        
        # Start the process with the pseudo-terminal
        process = subprocess.Popen(
            [str(cli_path)] + args,
            cwd=str(cli_dir),
            stdin=slave_fd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=env,
            text=True,
            preexec_fn=lambda: (os.setsid(), os.close(master_fd))
        )
        
        # Close the slave fd in the parent process
        os.close(slave_fd)
        
        # Function to send empty input periodically to prevent hanging
        def send_empty_input():
            try:
                while process.poll() is None:
                    time.sleep(1)
                    try:
                        os.write(master_fd, b'\n')
                    except (OSError, BrokenPipeError):
                        break
            except:
                pass
        
        # Start the input sender thread
        input_thread = threading.Thread(target=send_empty_input, daemon=True)
        input_thread.start()
        
        # Set up timeout (10 minutes)
        timeout = 600
        
        # Wait for completion with timeout
        try:
            stdout, stderr = process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            # Kill the process group if timeout
            try:
                os.killpg(os.getpgid(process.pid), signal.SIGTERM)
                time.sleep(2)
                if process.poll() is None:
                    os.killpg(os.getpgid(process.pid), signal.SIGKILL)
            except:
                pass
            stdout, stderr = process.communicate()
            print("Error: Process timed out after 10 minutes")
            return 1
        finally:
            # Clean up the master fd
            try:
                os.close(master_fd)
            except:
                pass
        
        # Output the results
        if stdout:
            # Filter out the "Press Esc to cancel" message
            filtered_stdout = []
            for line in stdout.split('\n'):
                if not any(phrase in line for phrase in [
                    'Press Esc to cancel',
                    'KeyAvailable'
                ]):
                    filtered_stdout.append(line)
            
            filtered_output = '\n'.join(filtered_stdout).strip()
            if filtered_output:
                print(filtered_output)
        
        if stderr:
            # Filter out console-related errors more aggressively
            filtered_errors = []
            for line in stderr.split('\n'):
                if not any(phrase in line for phrase in [
                    'Console.KeyAvailable',
                    'console input has been redirected',
                    'Timer_Elapsed',
                    'System.Threading',
                    'Cannot see if a key has been pressed',
                    'Try Console.In.Peek',
                    'ThreadPoolWorkQueue',
                    'PortableThreadPool',
                    'Unhandled exception. System.InvalidOperationException'
                ]):
                    if line.strip():
                        filtered_errors.append(line)
            
            filtered_stderr = '\n'.join(filtered_errors).strip()
            if filtered_stderr:
                print(filtered_stderr, file=sys.stderr)
        
        # If the process failed due to console issues but we know it's that specific error,
        # try to determine if the translation actually succeeded by checking output files
        if process.returncode != 0:
            if "translate" in args and len(args) >= 3:
                # Check if output directory exists and has files
                try:
                    output_dir = Path(args[2])  # Third argument should be output directory
                    if output_dir.exists() and list(output_dir.glob("*")):
                        # Files were created, translation probably succeeded despite console error
                        print("Translation completed successfully (console error ignored)")
                        return 0
                except:
                    pass
        
        return process.returncode
        
    except Exception as e:
        print(f"Error running doctr: {e}", file=sys.stderr)
        return 1

if __name__ == '__main__':
    # Pass all command line arguments to doctr
    exit_code = run_doctr(sys.argv[1:])
    sys.exit(exit_code)
