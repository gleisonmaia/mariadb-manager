# Gera src/MesasLog.Wpf/Resources/mysqlclient-bundle.zip a partir da pasta bin do cliente MariaDB/MySQL (Windows).
# Ex.: .\tools\pack-mysql-client.ps1 -BinDirectory "C:\Program Files\MariaDB 11.4\bin"
param(
    [Parameter(Mandatory = $true)]
    [string] $BinDirectory
)
$ErrorActionPreference = 'Stop'
$BinDirectory = (Resolve-Path $BinDirectory).Path
foreach ($f in @('mysqldump.exe', 'mysql.exe')) {
    if (-not (Test-Path (Join-Path $BinDirectory $f))) {
        throw "Arquivo nao encontrado: $f em $BinDirectory"
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repoRoot 'src\MesasLog.Wpf\Resources'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$zip = Join-Path $outDir 'mysqlclient-bundle.zip'
if (Test-Path $zip) { Remove-Item -LiteralPath $zip }

$staging = Join-Path $env:TEMP ("mysqlpack_" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    Copy-Item (Join-Path $BinDirectory 'mysqldump.exe') $staging
    Copy-Item (Join-Path $BinDirectory 'mysql.exe') $staging
    Get-ChildItem $BinDirectory -Filter '*.dll' | ForEach-Object { Copy-Item $_.FullName $staging }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -Force
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Criado: $zip"
Write-Host 'Recompile o projeto WPF para embutir o recurso.'
