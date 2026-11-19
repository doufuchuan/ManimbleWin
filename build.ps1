# 用正确位置的 .NET SDK 编译项目。
# 用法：在项目根目录执行  .\build.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sln  = Join-Path $root 'ManimbleWin.sln'
$sdkDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'

if (Test-Path $sdkDotnet) {
    $dotnet = $sdkDotnet
} else {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
}

if (-not $dotnet) {
    Write-Error '未找到 dotnet。请安装 .NET 8 SDK: https://aka.ms/dotnet/download'
    exit 1
}

Write-Host "Using dotnet: $dotnet"
& $dotnet --version
& $dotnet build $sln -c Debug --nologo
exit $LASTEXITCODE
