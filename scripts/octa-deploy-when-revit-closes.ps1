<#
.SYNOPSIS
OCTA: wait for Revit to close, back up the live Revit MCP install, then install a release bundle.

.DESCRIPTION
Polls every 30 s until no Revit.exe is running (up to -MaxHours), then:
  1. Copies the live add-in files and MCP server dist to C:\Dev\backups\<timestamp>\
  2. Runs the bundle's install.ps1 for Revit 2027 without touching the Claude Desktop
     config (it already points at the installed server with an absolute node path).
  3. Verifies the installed add-in DLL matches the bundle.
Restore = copy the backup folders back with Revit closed.
#>
param(
    [string]$Bundle = "C:\Dev\RevitMCPServer\release\RevitMCPServer-v0.8.40",
    [double]$MaxHours = 12
)
$ErrorActionPreference = "Stop"
$addinDir = "$env:APPDATA\Autodesk\Revit\Addins\2027"
$serverDir = "$env:LOCALAPPDATA\RevitMCPServer"
$deadline = (Get-Date).AddHours($MaxHours)

Write-Host "[$(Get-Date -Format T)] Waiting for Revit to close..."
while (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
    if ((Get-Date) -gt $deadline) { Write-Host "Gave up after $MaxHours h; nothing installed."; exit 2 }
    Start-Sleep -Seconds 30
}
Start-Sleep -Seconds 10   # let Revit release file handles

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = "C:\Dev\backups\$stamp"
New-Item -ItemType Directory -Force "$backup\addin-2027", "$backup\server-dist" | Out-Null
Copy-Item "$addinDir\RevitMCP*" "$backup\addin-2027\"
Copy-Item "$serverDir\dist\*" "$backup\server-dist\" -Recurse
Copy-Item "$serverDir\package.json" "$backup\"
Write-Host "[$(Get-Date -Format T)] Backed up current install to $backup"

& powershell -ExecutionPolicy Bypass -File "$Bundle\install.ps1" -RevitVersions 2027 -NoClientConfig
if ($LASTEXITCODE -ne 0) { Write-Host "install.ps1 failed (exit $LASTEXITCODE). Backup is at $backup"; exit 1 }

$want = (Get-FileHash "$Bundle\addin\2027\RevitMCPAddin.dll").Hash
$got = (Get-FileHash "$addinDir\RevitMCPAddin.dll").Hash
if ($want -ne $got) { Write-Host "VERIFY FAILED: installed add-in differs from bundle. Backup at $backup"; exit 1 }
Write-Host "[$(Get-Date -Format T)] Installed and verified. Restart Claude Desktop, then open Revit."
