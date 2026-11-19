#!/usr/bin/env bash
# 用正确位置的 .NET SDK 编译项目。用法：在项目根目录执行  ./build.sh
set -e
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SLN="$ROOT/ManimbleWin.sln"
SDK_DOTNET="$USERPROFILE/.dotnet/dotnet.exe"

if [ -f "$SDK_DOTNET" ]; then
    DOTNET="$SDK_DOTNET"
elif command -v dotnet >/dev/null 2>&1; then
    DOTNET="$(command -v dotnet)"
else
    echo "未找到 dotnet。请安装 .NET 8 SDK: https://aka.ms/dotnet/download" >&2
    exit 1
fi

echo "Using dotnet: $DOTNET"
"$DOTNET" --version
"$DOTNET" build "$SLN" -c Debug --nologo
