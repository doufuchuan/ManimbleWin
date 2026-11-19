#!/usr/bin/env bash
# 一键发布自包含单文件 Windows x64 程序到 dist/（Git Bash 版本）。
# 用法：在项目根目录执行  ./publish.sh
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
proj="$root/src/ManimbleWin/ManimbleWin.csproj"
dist="$root/dist"

# 本机 PATH 上的 dotnet 可能只有运行时；优先使用用户目录下的 SDK。
sdk_dotnet="${USERPROFILE:-$HOME}/.dotnet/dotnet.exe"
if [ -x "$sdk_dotnet" ]; then
    dotnet_bin="$sdk_dotnet"
elif command -v dotnet >/dev/null 2>&1; then
    dotnet_bin="$(command -v dotnet)"
else
    echo "未找到 dotnet SDK。请安装 .NET 8 SDK: https://aka.ms/dotnet/download" >&2
    exit 1
fi

if [ ! -f "$proj" ]; then
    echo "未找到项目文件: $proj" >&2
    exit 1
fi

echo "Using dotnet: $dotnet_bin"
"$dotnet_bin" --version

# Git Bash 的 /c/... 路径需转成 Windows 路径再传给 dotnet.exe。
to_win_path() {
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$1"
    else
        printf '%s' "$1"
    fi
}

proj_win="$(to_win_path "$proj")"
dist_win="$(to_win_path "$dist")"

rm -rf "$dist"
mkdir -p "$dist"

echo "Publishing self-contained single-file win-x64 executable..."
"$dotnet_bin" publish "$proj_win" \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    --nologo \
    -o "$dist_win"

exe="$dist/ManimbleWin.exe"
if [ ! -f "$exe" ]; then
    echo "发布产物不存在: $exe" >&2
    exit 1
fi

size_bytes=$(wc -c < "$exe" | tr -d ' ')
size_mb=$(awk "BEGIN { printf \"%.2f\", $size_bytes / 1048576 }")
echo ""
echo "Published executable: $exe"
echo "Size: ${size_bytes} bytes (${size_mb} MB)"
echo "Dist directory: $dist"
