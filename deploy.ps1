#!/usr/bin/env pwsh
# deploy.ps1 — Build e deploy da Meduza API no servidor de produção
# Uso: .\deploy.ps1

$ErrorActionPreference = "Stop"

$Server    = "root@192.168.1.148"
$RemoteDir = "/opt/meduza-api"
$Project   = "src/Meduza.Api/Meduza.Api.csproj"
$OutDir    = "artifacts/publish/meduza-api"
$Service   = "meduza-api"

Write-Host "==> [1/3] Publicando para linux-x64..." -ForegroundColor Cyan
dotnet publish $Project -c Release -r linux-x64 --self-contained false -o $OutDir /p:UseAppHost=true
if ($LASTEXITCODE -ne 0) { Write-Error "Publish falhou."; exit 1 }

# Remove appsettings do artefato para nao sobrescrever a config do servidor
Remove-Item -Force -ErrorAction SilentlyContinue "$OutDir/appsettings.json", "$OutDir/appsettings.*.json"

Write-Host "==> [2/3] Copiando arquivos para $Server`:$RemoteDir ..." -ForegroundColor Cyan
scp -r "$OutDir/*" "${Server}:${RemoteDir}/"
if ($LASTEXITCODE -ne 0) { Write-Error "Copia falhou."; exit 1 }

Write-Host "==> [3/3] Reiniciando servico $Service ..." -ForegroundColor Cyan
ssh $Server "chmod +x $RemoteDir/Meduza.Api; systemctl restart $Service; sleep 3; systemctl is-active $Service"
if ($LASTEXITCODE -ne 0) { Write-Error "Reinicio falhou."; exit 1 }

Write-Host "==> Deploy concluido com sucesso!" -ForegroundColor Green
