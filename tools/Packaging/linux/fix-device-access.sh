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
#
# 退出码：0 = 承诺的步骤全部完成；非 0 = 有步骤无法完成（工具缺失或执行失败）。
# 应用据此刻画结果：非 0 时降级为可复制的手动命令，而不是宣称修复成功——半完成状态
# 必须如实上报，即使规则文件此时已经落盘（手动命令幂等，用户路径仍然正确）。
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
  # 规则已写入但无法加载：这是一次未完成的修复，必须非 0 退出让应用降级，
  # 而不是 exit 0 让应用宣称"已修复"。
  echo "未找到 udevadm，无法重载 udev 规则（规则已写入 $rules_target，但尚未生效）" >&2
  exit 1
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
  # 同上：组变更是承诺的步骤之一，做不到就非 0 退出，由应用给出手动命令。
  echo "未找到 usermod，无法把 $user 加入 input 组" >&2
  exit 1
fi
