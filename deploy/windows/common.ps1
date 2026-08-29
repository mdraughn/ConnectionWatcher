# Shared helpers for the root-level Build/Publish/Install/Uninstall wrappers.
# Dot-source this; do not run directly.

$script:Vendor = "DSoftCo"
$script:ProductName = "ConnectionWatcher"
$script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")

function Get-InstallRoot([string]$Override = "") {
    if ($Override) { return $Override }
    Join-Path $env:ProgramFiles "$script:Vendor\$script:ProductName"
}

function Get-DefaultDataDir([string]$RootOverride = "") {
    # Program Files stays read-only at runtime; data belongs in ProgramData.
    if ($RootOverride) { return Join-Path $RootOverride "data" }
    Join-Path $env:ProgramData "$script:Vendor\$script:ProductName\data"
}

function Test-IsAdmin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-SelfElevated([string]$ScriptPath, [string[]]$ScriptArgs = @()) {
    # Relaunch elevated with a window that stays open so output is readable.
    $argList = @("-NoExit", "-ExecutionPolicy", "Bypass", "-File", "`"$ScriptPath`"") + $ScriptArgs
    Start-Process powershell -Verb RunAs -ArgumentList $argList
}

function Publish-WatcherTo([string]$OutDir) {
    $service = Get-Service -Name "ConnectionWatcher" -ErrorAction SilentlyContinue
    $wasRunning = $service -and $service.Status -eq "Running"
    if ($wasRunning) {
        Write-Host "Stopping the ConnectionWatcher service for the update..."
        Stop-Service "ConnectionWatcher" -Force
    }
    dotnet publish (Join-Path $script:RepoRoot "Programs\ConWatch") -c Release -o $OutDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
    if ($wasRunning) {
        Start-Service "ConnectionWatcher"
        Write-Host "Service restarted."
    }
}

function Publish-TrayTo([string]$OutDir) {
    $wasRunning = [bool](Get-Process -Name "ConTray" -ErrorAction SilentlyContinue)
    if ($wasRunning) {
        Get-Process -Name "ConTray" | Stop-Process -Force
    }
    dotnet publish (Join-Path $script:RepoRoot "Programs\ConTray") -c Release -o $OutDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
    if ($wasRunning) {
        Start-Process (Join-Path $OutDir "ConTray.exe")
        Write-Host "Tray restarted."
    }
}

function Get-InstalledWatcherUrl {
    # Reads the pinned --urls from the installed service definition, if any.
    $service = Get-CimInstance Win32_Service -Filter "Name='ConnectionWatcher'" -ErrorAction SilentlyContinue
    if ($service -and $service.PathName -match "--urls\s+\S*:(\d+)") {
        return "http://localhost:$($Matches[1])"
    }
    return $null
}
