# Cria o repositório em github.com e envia a branch main (após autenticação).
# 1) Autenticar uma vez:  gh auth login
#    Ou: $env:GH_TOKEN = "seu_token"  (escopos: repo; fine-grained: Contents read/write)
# 2) Na raiz do projeto:  .\repo-remoto.ps1
#
param(
    [string]$NomeRepo = "mariadb-manager",
    [ValidateSet("public", "private")]
    [string]$Visibilidade = "private"
)

$ErrorActionPreference = "Stop"
$raiz = $PSScriptRoot
$gh = "${env:ProgramFiles}\GitHub CLI\gh.exe"
if (-not (Test-Path -LiteralPath $gh)) {
    Write-Error "GitHub CLI não encontrado em '$gh'. Instale: winget install GitHub.cli --source winget"
}

$env:Path = "$(Split-Path $gh -Parent);$env:Path"

Push-Location $raiz
try {
    & $gh auth status 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Execute primeiro: gh auth login" -ForegroundColor Yellow
        Write-Host "Ou defina GH_TOKEN e volte a executar este script." -ForegroundColor Yellow
        exit 1
    }

    $temOrigin = $false
    try {
        git remote get-url origin 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $temOrigin = $true }
    } catch { }

    if ($temOrigin) {
        Write-Host "Remote 'origin' já existe. A fazer push da branch main..."
        git push -u origin main
    }
    else {
        & $gh repo create $NomeRepo --$Visibilidade --source=. --remote=origin --push
    }

    Write-Host "Concluído." -ForegroundColor Green
    git remote -v
}
finally {
    Pop-Location
}
