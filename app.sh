#!/usr/bin/env bash
# QuickPackage 一键构建并启动脚本
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_DIR="$PROJECT_DIR/build"

cd "$PROJECT_DIR"

if [ ! -f "$BUILD_DIR/quickpackage" ]; then
    echo "==> 配置项目 (首次)…"
    cmake -B "$BUILD_DIR" -DCMAKE_BUILD_TYPE=Release
fi

echo "==> 编译…"
cmake --build "$BUILD_DIR" -j"$(nproc)"

# 安装到用户目录,让 KDE 能找到 desktop/notifyrc/kxmlgui 资源文件
echo "==> 安装资源到 ~/.local …"
cmake --install "$BUILD_DIR" --prefix "$HOME/.local" >/dev/null

echo "==> 启动 QuickPackage…"
if [ -x "$HOME/.local/bin/quickpackage" ]; then
    exec "$HOME/.local/bin/quickpackage" "$@"
else
    exec "$BUILD_DIR/quickpackage" "$@"
fi
