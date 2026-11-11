#!/usr/bin/env bash
# DeziroJoy 设备访问一键修复 helper。
#
# 由应用通过 pkexec 以 root 身份运行（用户在设置卡片点击「修复设备访问权限」）。
# 规则正文由应用生成后写入临时文件、以参数传入：单一来源是应用内的
# LinuxDeviceAccess.UdevRulesText，这里不保留第二份会漂移的副本。
#
# 用法：fix-device-access.sh <rules-file>
#
# 动作（全部是用户显式 opt-in：安装器从不注册 udev 规则，见 docs/ELEVATION-AND-DAEMON.md）：
#   1. 安装 /etc/udev/rules.d/99-dezirojoy.rules；
#   2. udevadm control --reload && udevadm trigger（重载即生效，无需重启）；
#   3. usermod -aG input <调用者>（组变更要重新登录后生效）。
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
  echo "需要 root 权限：此脚本由 DeziroJoy 通过 pkexec 调用，不应直接以普通用户运行。" >&2
  exit 1
fi

if [ "$#" -ne 1 ] || [ ! -f "$1" ]; then
  echo "usage: $0 <rules-file>" >&2
  exit 2
fi

rules_source="$1"
rules_target="/etc/udev/rules.d/99-dezirojoy.rules"

install -m 0644 "$rules_source" "$rules_target"
echo "已写入 $rules_target"

if command -v udevadm >/dev/null 2>&1; then
  udevadm control --reload
  udevadm trigger --subsystem-match=input
  echo "已重载 udev 规则"
else
  echo "未找到 udevadm，跳过重载（规则已写入，重启或手动 udevadm control --reload 后生效）" >&2
fi

# pkexec 不设置 SUDO_USER；它把调用者的 uid 放在 PKEXEC_UID 里。
user="${SUDO_USER:-}"
if [ -z "$user" ] && [ -n "${PKEXEC_UID:-}" ]; then
  user="$(id -nu "$PKEXEC_UID" 2>/dev/null || true)"
fi
if [ -z "$user" ]; then
  user="$(id -un)"
fi

if [ "$user" = "root" ]; then
  echo "调用者是 root，无需加入 input 组" >&2
elif command -v usermod >/dev/null 2>&1; then
  usermod -aG input "$user"
  echo "已将 $user 加入 input 组（重新登录后生效）"
else
  echo "未找到 usermod，请手动执行：usermod -aG input $user" >&2
fi
