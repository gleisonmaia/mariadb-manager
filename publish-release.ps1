# Publica: MariaDBLogExplorer.Launcher.exe (self-contained, na raiz) + app\MariaDBLogExplorer.exe (WPF, single-file FDD: DLLs da app no .exe, runtime .NET não embutido).
# O launcher verifica o Windows Desktop Runtime 8, baixa/instala se necessário e inicia o WPF.
# appsettings.json e mesas.sql estão embutidos no WPF; pode haver appsettings.json opcional em app\.

param(
    [string]$Output = "publish",
    [string]$Rid = "win-x64",
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

Write-Host "Publicando launcher (self-contained, arquivo único comprimido)..."
dotnet publish (Join-Path $root "src\MesasLog.Bootstrapper\MesasLog.Bootstrapper.csproj") `
    -c Release -r $Rid --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -o $outPath

Write-Host "Publicando WPF (single-file framework-dependent: DLLs no .exe, sem runtime embutido)..."
# NETSDK1176: EnableCompressionInSingleFile só com self-contained; FDD usa single-file sem compressão.
dotnet publish (Join-Path $root "src\MesasLog.Wpf\MesasLog.Wpf.csproj") `
    -c Release -r $Rid `
    --self-contained false `
    -p:SelfContained=false `
    -p:PublishSingleFile=true `
    -p:DebugType=none `
    -o $appPath

Get-ChildItem -LiteralPath $outPath -Filter *.pdb -File -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

foreach ($extra in @('appsettings.json', 'mesas.sql')) {
    $p = Join-Path $appPath $extra
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
}

$example = Join-Path $root "src\MesasLog.Bootstrapper\bootstrapper.settings.example.json"
$settingsDest = Join-Path $appPath "bootstrapper.settings.json"
if (Test-Path $example) {
    Copy-Item -Path $example -Destination $settingsDest -Force
}

$batPath = Join-Path $appPath "Iniciar Maria DB Log Explorer.bat"
@'
@echo off
start "" "%~dp0..\MariaDBLogExplorer.Launcher.exe" %*
'@ | Set-Content -Path $batPath -Encoding ascii

if (-not $NoShortcut -and $env:OS -match 'Windows') {
    try {
        $launcher = Join-Path $outPath "MariaDBLogExplorer.Launcher.exe"
        $lnkPath = Join-Path $appPath "Maria DB Log Explorer.lnk"
        $w = New-Object -ComObject WScript.Shell
        $s = $w.CreateShortcut($lnkPath)
        $s.TargetPath = $launcher
        $s.WorkingDirectory = $outPath
        $s.Description = "Maria DB - Log Explorer (launcher)"
        $s.Save()
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($s) | Out-Null
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($w) | Out-Null
    }
    catch {
        Write-Warning "Não foi possível criar Maria DB Log Explorer.lnk: $($_.Exception.Message)"
    }
}

$launcherExe = Join-Path $outPath "MariaDBLogExplorer.Launcher.exe"
$wpfExe = Join-Path $appPath "MariaDBLogExplorer.exe"
if (Test-Path -LiteralPath $launcherExe) {
    $lnMb = [math]::Round((Get-Item $launcherExe).Length / 1MB, 2)
    Write-Host "Launcher: ~ $lnMb MB (inclui runtime .NET para o próprio console)"
}
if (Test-Path -LiteralPath $wpfExe) {
    $wpfMb = [math]::Round((Get-Item $wpfExe).Length / 1MB, 2)
    Write-Host "WPF: ~ $wpfMb MB (single-file; usa Desktop Runtime instalado no Windows)"
}

Write-Host ""
Write-Host "Concluído: $outPath"
Write-Host "Distribua a pasta completa. O utilizador abre MariaDBLogExplorer.Launcher.exe (raiz). Opcional: appsettings.json em app\ para personalizar."
