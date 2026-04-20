# Publica: MariaDBManager.Launcher.exe + app\MariaDBManager.exe (.NET Framework 4.8).
# O WPF é empacotado com Costura.Fody: dependências geridas fundidas num único .exe (sem DLLs satélite).
# O launcher verifica o .NET Framework 4.8 no registro; se não houver, oferece abrir a página de download.
# appsettings.json e mesas.sql estão embutidos no assembly; pode haver appsettings.json opcional em app\.

param(
    [string]$Output = "publish",
    [switch]$NoShortcut
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not $root) {
    $root = (Get-Location).Path
}

$outPath = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }
$appPath = Join-Path $outPath "app"

Write-Host "Limpando pasta de publicação: $outPath"
if (Test-Path -LiteralPath $outPath) {
    Remove-Item -LiteralPath $outPath -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $appPath | Out-Null

Write-Host "Publicando launcher (console, .NET Framework 4.8)..."
dotnet publish (Join-Path $root "src\MesasLog.Bootstrapper\MesasLog.Bootstrapper.csproj") `
    -c Release `
    -p:DebugType=none `
    -o $outPath

Write-Host "Publicando WPF (.NET Framework 4.8)..."
dotnet publish (Join-Path $root "src\MesasLog.Wpf\MesasLog.Wpf.csproj") `
    -c Release `
    -p:DebugType=none `
    -o $appPath

# Config do launcher: na raiz fica só o .exe; o .config vai para app\ (o programa define APP_CONFIG_FILE em runtime).
$launcherConfigSrc = Join-Path $outPath "MariaDBManager.Launcher.exe.config"
$launcherConfigDest = Join-Path $appPath "MariaDBManager.Launcher.exe.config"
if (Test-Path -LiteralPath $launcherConfigSrc) {
    Move-Item -LiteralPath $launcherConfigSrc -Destination $launcherConfigDest -Force
}

# Raiz: apenas MariaDBManager.Launcher.exe
Get-ChildItem -LiteralPath $outPath -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne "MariaDBManager.Launcher.exe" } |
    Remove-Item -Force -ErrorAction SilentlyContinue

Get-ChildItem -LiteralPath $outPath -Filter *.pdb -File -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

# Costura funde DLLs geridas; remover qualquer .dll residual (não deve existir em Release).
Get-ChildItem -LiteralPath $outPath -Filter *.dll -File -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

foreach ($extra in @('appsettings.json', 'mesas.sql')) {
    $p = Join-Path $appPath $extra
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
}

$example = Join-Path $root "src\MesasLog.Bootstrapper\bootstrapper.settings.example.json"
$settingsDest = Join-Path $appPath "bootstrapper.settings.json"
if (Test-Path $example) {
    Copy-Item -Path $example -Destination $settingsDest -Force
}

$batPath = Join-Path $appPath "Iniciar MariaDB Manager.bat"
@'
@echo off
start "" "%~dp0..\MariaDBManager.Launcher.exe" %*
'@ | Set-Content -Path $batPath -Encoding ascii

if (-not $NoShortcut -and $env:OS -match 'Windows') {
    try {
        $launcher = Join-Path $outPath "MariaDBManager.Launcher.exe"
        $lnkPath = Join-Path $appPath "MariaDB Manager.lnk"
        $w = New-Object -ComObject WScript.Shell
        $s = $w.CreateShortcut($lnkPath)
        $s.TargetPath = $launcher
        $s.WorkingDirectory = $outPath
        $s.Description = "MariaDB Manager (launcher)"
        $s.Save()
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($s) | Out-Null
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($w) | Out-Null
    }
    catch {
        Write-Warning "Não foi possível criar MariaDB Manager.lnk: $($_.Exception.Message)"
    }
}

$launcherExe = Join-Path $outPath "MariaDBManager.Launcher.exe"
$wpfExe = Join-Path $appPath "MariaDBManager.exe"
if (Test-Path -LiteralPath $launcherExe) {
    $lnKb = [math]::Round((Get-Item $launcherExe).Length / 1KB, 1)
    Write-Host "Launcher: ~ $lnKb KB"
}
if (Test-Path -LiteralPath $wpfExe) {
    $wpfMb = [math]::Round((Get-Item $wpfExe).Length / 1MB, 2)
    Write-Host "WPF: ~ $wpfMb MB (single-file Costura: DLLs geridas embutidas no .exe)"
}

Write-Host ""
Write-Host "Concluído: $outPath"
Write-Host "Distribua a pasta completa. O utilizador abre MariaDBManager.Launcher.exe (raiz). Opcional: appsettings.json em app\ para personalizar."
