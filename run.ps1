# Galgame 语录收藏工具 - 启动脚本
# 快捷键: Ctrl+Win+Z
# 用法: 右键 -> 使用 PowerShell 运行

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Galgame 语录收藏工具" -ForegroundColor Cyan
Write-Host "  快捷键: Ctrl+Win+Z" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$ProjectDir = Join-Path $PSScriptRoot "GalgameQuoteCollector"

# 检查 dotnet SDK
try {
    $version = dotnet --version 2>&1
    if (-not $?) { throw "not found" }
    Write-Host ".NET SDK $version 已就绪" -ForegroundColor Green
} catch {
    Write-Host "[错误] 未安装 .NET SDK，请先安装 .NET 8 SDK" -ForegroundColor Red
    Write-Host "https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0" -ForegroundColor Yellow
    Read-Host "按 Enter 退出"
    exit 1
}

# 杀死残留进程
Get-Process GalgameQuoteCollector -ErrorAction SilentlyContinue | Stop-Process -Force

# 构建并运行
Set-Location $ProjectDir
Write-Host "[正在启动...]" -ForegroundColor Yellow

$result = dotnet run --no-build 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "[首次启动，正在编译...]" -ForegroundColor Yellow
    dotnet run
}

Read-Host "按 Enter 退出"
