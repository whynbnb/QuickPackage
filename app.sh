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

# GIO/xdg-desktop-portal 会校验 Exec 指向的二进制是否存在,
# ~/.local/bin 不一定在 PATH 中,因此改写为绝对路径
sed -i "s|^Exec=quickpackage.*|Exec=$HOME/.local/bin/quickpackage|" \
    "$HOME/.local/share/applications/org.quickpackage.QuickPackage.desktop"
update-desktop-database "$HOME/.local/share/applications" 2>/dev/null || true

export PATH="$HOME/.local/bin:$PATH"

echo "==> 启动 QuickPackage…"
if [ -x "$HOME/.local/bin/quickpackage" ]; then
    exec "$HOME/.local/bin/quickpackage" "$@"
else
    exec "$BUILD_DIR/quickpackage" "$@"
fi
