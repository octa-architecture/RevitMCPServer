<#
.SYNOPSIS
OCTA: install a release bundle of the Revit MCP - waiting for Revit to close, or closing and
reopening it - with a backup first.

.DESCRIPTION
  1. If -CloseRevit: asks the add-in to exit Revit (saving only with -Save; otherwise it refuses
     when anything is unsaved). Without -CloseRevit, waits for the user to close Revit.
  2. Backs up the live add-in and MCP server to C:\Dev\backups\<timestamp>\
  3. Signs the add-in DLLs with "OCTA Code Signing" if that certificate exists
     (see octa-trust-setup.ps1), so Revit loads them without the security prompt.
  4. Runs the bundle's install.ps1 for Revit 2027 without touching Claude's config.
  5. Verifies the install; with -Reopen, relaunches Revit with the documents that were open.
Restore = copy the backup folders back with Revit closed.
#>
param(
    [string]$Bundle = "C:\Dev\RevitMCPServer\release\RevitMCPServer-v0.8.40",
    [double]$MaxHours = 12,
    [switch]$CloseRevit,
    [switch]$Save,
    [switch]$Reopen
)
$ErrorActionPreference = "Stop"
$ver = "2027"
$addinDir = "$env:APPDATA\Autodesk\Revit\Addins\$ver"
$serverDir = "$env:LOCALAPPDATA\RevitMCPServer"
$revitExe = "C:\Program Files\Autodesk\Revit $ver\Revit.exe"
$base = "http://127.0.0.1:7892"

function Call-Addin([string]$command, [hashtable]$params) {
    $tok = (Get-Content "$addinDir\revit-mcp-token.txt" -Raw).Trim()
    $body = @{ command = $command; params = $params } | ConvertTo-Json -Depth 5
    Invoke-RestMethod -Uri "$base/mcp" -Method Post -Headers @{ Authorization = "Bearer $tok" } `
        -ContentType "application/json" -Body $body -TimeoutSec 120
}

$reopenPaths = @()
if ($CloseRevit -and (Get-Process -Name Revit -ErrorAction SilentlyContinue)) {
    try {
        $docs = Call-Addin "list_open_documents" @{}
        $reopenPaths = @($docs.data.documents | Where-Object { $_.path } | ForEach-Object { $_.path })
        $r = Call-Addin "exit_revit" @{ save = [bool]$Save }
        if (-not $r.ok) { Write-Host "Revit refused to exit: $($r.error.message)"; exit 3 }
        Write-Host "[$(Get-Date -Format T)] Asked Revit to exit (saved: $($r.data.saved -join ', '))"
    } catch {
        Write-Host "Couldn't ask Revit to exit ($($_.Exception.Message)). Close it manually."; exit 3
    }
}

$deadline = (Get-Date).AddHours($MaxHours)
Write-Host "[$(Get-Date -Format T)] Waiting for Revit to close..."
while (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
    if ((Get-Date) -gt $deadline) { Write-Host "Gave up after $MaxHours h; nothing installed."; exit 2 }
    Start-Sleep -Seconds 5
}
Start-Sleep -Seconds 10   # let Revit release file handles

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = "C:\Dev\backups\$stamp"
New-Item -ItemType Directory -Force "$backup\addin-$ver", "$backup\server-dist" | Out-Null
Copy-Item "$addinDir\RevitMCP*" "$backup\addin-$ver\"
Copy-Item "$serverDir\dist\*" "$backup\server-dist\" -Recurse
Copy-Item "$serverDir\package.json" "$backup\"
Write-Host "[$(Get-Date -Format T)] Backed up current install to $backup"

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object Subject -eq "CN=OCTA Code Signing" | Select-Object -First 1
if ($cert) {
    foreach ($dll in Get-ChildItem "$Bundle\addin\$ver\RevitMCP*.dll") {
        $s = Set-AuthenticodeSignature -FilePath $dll.FullName -Certificate $cert -HashAlgorithm SHA256
        Write-Host "Signed $($dll.Name): $($s.Status)"
    }
} else {
    Write-Host "No 'OCTA Code Signing' certificate - DLLs unsigned; Revit will show its add-in prompt once."
}

& powershell -ExecutionPolicy Bypass -File "$Bundle\install.ps1" -RevitVersions $ver -NoClientConfig | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "install.ps1 failed (exit $LASTEXITCODE). Backup is at $backup"; exit 1 }

$want = (Get-FileHash "$Bundle\addin\$ver\RevitMCPAddin.dll").Hash
$got = (Get-FileHash "$addinDir\RevitMCPAddin.dll").Hash
if ($want -ne $got) { Write-Host "VERIFY FAILED: installed add-in differs from bundle. Backup at $backup"; exit 1 }
Write-Host "[$(Get-Date -Format T)] Installed and verified."

if ($Reopen) {
    $model = $reopenPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($model) { Start-Process $revitExe -ArgumentList "`"$model`"" } else { Start-Process $revitExe }
    $end = (Get-Date).AddMinutes(5)
    do { Start-Sleep 5; try { $up = Invoke-RestMethod "$base/health" -TimeoutSec 3 } catch { $up = $null } } until ($up -or (Get-Date) -gt $end)
    if ($up) { Write-Host "[$(Get-Date -Format T)] Revit reopened: add-in v$($up.version), $($up.commandCount) commands." }
    else { Write-Host "Revit started but the add-in didn't answer within 5 min - a dialog may be waiting at the PC." }
}
Write-Host "Restart Claude Desktop (or the MCP server) to pick up new tools."
