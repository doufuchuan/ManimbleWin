# 一键发布自包含单文件 Windows x64 程序到 dist/。
# 用法：在项目根目录执行  .\publish.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'src\ManimbleWin\ManimbleWin.csproj'
$dist = Join-Path $root 'dist'

# 本机 PATH 上的 dotnet 可能只有运行时；优先使用用户目录下的 SDK。
$userProfile = $env:USERPROFILE
if (-not $userProfile) {
    $userProfile = [Environment]::GetFolderPath('UserProfile')
}
$sdkDotnet = Join-Path $userProfile '.dotnet\dotnet.exe'
if ($sdkDotnet -and (Test-Path $sdkDotnet)) {
    $dotnet = $sdkDotnet
} else {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
}

if (-not $dotnet) {
    Write-Error '未找到 dotnet SDK。请安装 .NET 8 SDK: https://aka.ms/dotnet/download'
    exit 1
}

if (-not (Test-Path $proj)) {
    Write-Error "未找到项目文件: $proj"
    exit 1
}

Write-Host "Using dotnet: $dotnet"
& $dotnet --version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (Test-Path $dist) {
    Write-Host "Cleaning output: $dist"
    Remove-Item -Path $dist -Recurse -Force
}
New-Item -ItemType Directory -Path $dist -Force | Out-Null

Write-Host 'Publishing self-contained single-file win-x64 executable...'
& $dotnet publish $proj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --nologo `
    -o $dist

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish 失败，退出码: $LASTEXITCODE"
    exit $LASTEXITCODE
}

$exe = Join-Path $dist 'ManimbleWin.exe'
if (-not (Test-Path $exe)) {
    Write-Error "发布产物不存在: $exe"
    exit 1
}

$item = Get-Item $exe
$sizeMB = [math]::Round($item.Length / 1MB, 2)
Write-Host ''
Write-Host "Published executable: $($item.FullName)"
Write-Host "Size: $($item.Length) bytes ($sizeMB MB)"
Write-Host "Dist directory: $dist"
exit 0
