# Registers the .crproj file association so double-clicking a Crowbar project
# file launches the editor with that project (the file path is passed as the
# first command-line argument, which the editor opens at startup).
#
# The association is written to HKEY_CURRENT_USER (Software\Classes), so it
# applies to the current user only and needs no admin rights. Run again after
# moving the editor, or pass -EditorPath to point at a specific build.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\RegisterCrowbarProject.ps1
#   powershell -ExecutionPolicy Bypass -File tools\RegisterCrowbarProject.ps1 -EditorPath "C:\Crowbar\Crowbar.Editor.exe"
#   powershell -ExecutionPolicy Bypass -File tools\RegisterCrowbarProject.ps1 -Unregister
param(
    [string]$EditorPath = "",
    [switch]$Unregister
)

$ErrorActionPreference = "Stop"

$extension = ".crproj"
$progId = "Crowbar.Project"
$baseKey = "HKCU:\Software\Classes"

if ($Unregister) {
    Remove-Item -Path "$baseKey\$extension" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "$baseKey\$progId" -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Removed the .crproj association ($extension -> $progId)."
    exit 0
}

if ([string]::IsNullOrWhiteSpace($EditorPath)) {
    # Default: the Debug build of the editor next to the repo.
    $EditorPath = Join-Path $PSScriptRoot "..\src\Editor\bin\Debug\net11.0\Crowbar.Editor.exe"
}
if (-not (Test-Path -LiteralPath $EditorPath)) {
    throw "Editor not found at '$EditorPath'. Build it first (dotnet build src\Editor) or pass -EditorPath."
}
$editor = (Resolve-Path -LiteralPath $EditorPath).Path

# .crproj -> ProgID
New-Item -Path "$baseKey\$extension" -Force | Out-Null
Set-ItemProperty -Path "$baseKey\$extension" -Name "(default)" -Value $progId

# ProgID: display name, icon and the open action that passes "%1" to the editor.
New-Item -Path "$baseKey\$progId" -Force | Out-Null
Set-ItemProperty -Path "$baseKey\$progId" -Name "(default)" -Value "Crowbar Project"

New-Item -Path "$baseKey\$progId\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$baseKey\$progId\DefaultIcon" -Name "(default)" -Value "`"$editor`,0`""

New-Item -Path "$baseKey\$progId\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$baseKey\$progId\shell\open\command" -Name "(default)" -Value "`"$editor`" `"%1`""

Write-Host "Registered .crproj -> $editor"
Write-Host "Double-click a .crproj file (or run '$editor <path>.crproj') to open it."