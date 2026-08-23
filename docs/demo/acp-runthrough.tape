# ps-agent — Invoke-Acp driving a live `opencode acp` session.
#
# Recorded with VHS (https://github.com/charmbracelet/vhs). VHS needs a real PTY, which on Windows
# means recording from WSL and launching `pwsh.exe` through interop — the console it gets is a real
# one (IsInputRedirected/IsOutputRedirected are both false), so the interactive viewer runs for
# real rather than falling back to the headless pipeline.
#
#   wsl -d Ubuntu-24.04 -- vhs docs/demo/acp-runthrough.tape
#
# Two rules learned the hard way, both of which fail silently rather than loudly:
#   * Quote every path in an Output line — VHS's parser splits an unquoted one.
#   * Never put `$` in a Type line. The text is typed into bash first, so `$env:TEMP` is expanded
#     away before pwsh ever sees it. Use literal Windows paths.
#
# The two absolute paths below are the only machine-specific things here: the built module and this
# repo's demo workspace. Adjust both for your checkout.

Output "/tmp/ps-agent-acp.gif"
Output "/tmp/ps-agent-acp.mp4"

Set Shell bash
Set FontSize 16
Set Width 1200
Set Height 700
Set Padding 14
Set TypingSpeed 45ms

# --- setup, kept off camera -------------------------------------------------
# Also sets the cwd explicitly: launched from WSL the process starts on a
# \\wsl.localhost UNC path, which is not a usable workspace root.
Hide
Type 'pwsh.exe -NoLogo -NoProfile -NoExit -Command "Import-Module C:\Users\andyb\work\core\ps-agent\src\PsAgent.Cmdlets\bin\Debug\net10.0\PsAgent.psd1; Set-Location C:\Users\andyb\work\core\ps-agent\docs\demo\workspace; Clear-Host"'
Enter
Sleep 15s
Show

# --- what we are working with -----------------------------------------------
Type "Get-Content greet.ps1"
Enter
Sleep 4s

# --- the interactive viewer, driving a real opencode agent ------------------
Type "Invoke-Acp -Agent opencode -AutoApprove"
Enter
Sleep 12s

# `i` opens the prompt line
Type "i"
Sleep 1500ms
Type "Read greet.ps1 and tell me in one sentence what is wrong with it."
Sleep 1500ms
Enter

# opencode reads the file and answers; rows stream in as they arrive
Sleep 40s

# Move up to the tool call and expand it to see the detail
Up
Sleep 1500ms
Enter
Sleep 5s

# Collapse and leave
Enter
Sleep 1s
Type "q"
Sleep 4s
