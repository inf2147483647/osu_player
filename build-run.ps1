#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Release 构建并启动 OsuPlayer 桌面端。

.EXAMPLE
    .\build-run.ps1
#>
[CmdletBinding()]
param(
    # 只编译，不启动
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'OsuPlayer.Desktop\OsuPlayer.Desktop.csproj'
$outputDir = Join-Path $root 'OsuPlayer.Desktop\bin\Release\net10.0'
$exe = Join-Path $outputDir 'OsuPlayer.exe'

# 先结束正在运行的实例，否则 Release 输出目录里的 DLL 被占用会导致编译失败。
Get-Process -Name 'OsuPlayer' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "结束已运行的 OsuPlayer (PID $($_.Id))"
    $_.Kill()
    $_.WaitForExit(5000) | Out-Null
}

Write-Host 'Release 构建中...' -ForegroundColor Cyan
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host '构建失败' -ForegroundColor Red
    exit $LASTEXITCODE
}

if (-not (Test-Path $exe)) {
    Write-Host "构建产物不存在: $exe" -ForegroundColor Red
    exit 1
}

if ($NoLaunch) {
    Write-Host "构建完成: $exe" -ForegroundColor Green
    exit 0
}

Write-Host '启动 OsuPlayer...' -ForegroundColor Green
Start-Process -FilePath $exe -WorkingDirectory $outputDir
