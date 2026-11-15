#!/usr/bin/env python3
"""Render the checked baseline JSON/CSV as a Chinese Markdown report."""

from __future__ import annotations

import argparse
import csv
import datetime as dt
import json
import os
from pathlib import Path
import statistics
from typing import Any

MIB = 1024 * 1024
LABELS = {
    "no-controller-gui-exited": "无手柄／GUI 退出",
    "idle-gui-exited": "静止手柄／GUI 退出",
    "mouse-gui-exited": "持续鼠标／GUI 退出",
    "macro-gui-exited": "复杂宏／GUI 退出",
    "multi-gui-exited": "四设备持续鼠标／GUI 退出",
    "mouse-gui-open": "持续鼠标／GUI 打开",
    "mouse-overlay": "持续鼠标／浮层显示",
    "mouse-screen-keyboard": "持续鼠标／屏幕键盘显示",
    "mouse-continuous": "持续鼠标／连续 120 秒",
    "gui-toggle": "持续鼠标／GUI 开关 6 轮",
}
ROLE_LABELS = {"daemon": "daemon", "presentation": "呈现宿主", "gui": "GUI"}
ROLE_ORDER = {"daemon": 0, "presentation": 1, "gui": 2}


def number(value: float, digits: int = 2) -> str:
    return f"{value:.{digits}f}"


def memory_pair(role: dict[str, Any], average: str, peak: str) -> str:
    return f"{number(role[average])}／{number(role[peak])}"


def percentile(metric: dict[str, Any] | None, key: str, fraction: float) -> str:
    if not metric or not metric["count"]:
        return "—"
    value = number(metric[key])
    # RuntimePerformance returns max as a lower-bound marker when the requested
    # percentile lies in the overflow region.
    if metric["overflowCount"] / metric["count"] > 1 - fraction:
        return f"≥{value}"
    return value


def timing_cells(metric: dict[str, Any] | None) -> list[str]:
    if not metric or not metric["count"]:
        return ["—", "—", "—", "—", "0"]
    return [
        percentile(metric, "p50Ms", 0.50),
        percentile(metric, "p95Ms", 0.95),
        percentile(metric, "p99Ms", 0.99),
        number(metric["maxMs"]),
        str(metric["overflowCount"]),
    ]


def allocation_rate(scenario: dict[str, Any], role_name: str, scenarios_raw: list[dict[str, Any]]) -> str:
    if scenario["name"] == "gui-toggle" and role_name == "gui":
        return "不完整"
    raw = next(item for item in scenarios_raw if item["name"] == scenario["name"])
    role = next(role for role in scenario["roles"] if role["role"] == role_name)
    rate = 0.0
    for pid in role["pids"]:
        start = raw["metricsStart"].get(str(pid))
        end = raw["metricsEnd"].get(str(pid))
        if not start or not end:
            continue
        elapsed = (dt.datetime.fromisoformat(end["timestampUtc"]) - dt.datetime.fromisoformat(start["timestampUtc"])).total_seconds()
        if elapsed > 0:
            allocated = max(0, end["totalAllocatedBytes"] - start["totalAllocatedBytes"])
            rate += allocated / MIB / elapsed
    return number(rate, 3)


def total_memory(rows: list[dict[str, Any]], scenario: str) -> dict[str, float]:
    groups: dict[int, list[dict[str, Any]]] = {}
    for row in rows:
        if row["scenario"] == scenario:
            groups.setdefault(int(row["sampleIndex"]), []).append(row)
    working = [sum(int(row["workingSetBytes"]) for row in group) / MIB for group in groups.values()]
    private = [sum(int(row["privateBytes"]) for row in group) / MIB for group in groups.values()]
    return {
        "workingAverage": statistics.fmean(working),
        "workingPeak": max(working),
        "privateAverage": statistics.fmean(private),
        "privatePeak": max(private),
    }


def relative_link(target: Path, output: Path) -> str:
    return Path(os.path.relpath(target.resolve(), output.parent.resolve())).as_posix()


def render(data: Path, output: Path) -> str:
    metadata = json.loads((data / "metadata.json").read_text(encoding="utf-8"))
    summaries = json.loads((data / "summary.json").read_text(encoding="utf-8"))
    scenarios_raw = json.loads((data / "scenario-metrics.json").read_text(encoding="utf-8"))
    with (data / "resources.csv").open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    by_name = {scenario["name"]: scenario for scenario in summaries}
    started = dt.datetime.fromisoformat(metadata["startedUtc"]).astimezone(dt.timezone(dt.timedelta(hours=8)))
    order = [name for name in LABELS if name in by_name]
    totals = {name: total_memory(rows, name) for name in order}
    success = sum(by_name[name]["injectionSuccessCount"] for name in order)
    failures = sum(by_name[name]["injectionFailureCount"] for name in order)
    if failures:
        raise RuntimeError(f"Refusing to publish a passing baseline with {failures} failed SendInput calls")

    no_controller = by_name["no-controller-gui-exited"]
    mouse = by_name["mouse-gui-exited"]
    gui_open = by_name["mouse-gui-open"]
    no_controller_total = totals["no-controller-gui-exited"]
    mouse_total = totals["mouse-gui-exited"]
    gui_total = totals["mouse-gui-open"]
    daemon_idle = next(role for role in no_controller["roles"] if role["role"] == "daemon")
    presenter_idle = next(role for role in no_controller["roles"] if role["role"] == "presentation")
    continuous_daemon = [row for row in rows if row["scenario"] == "mouse-continuous" and row["role"] == "daemon"]
    continuous_working = [int(row["workingSetBytes"]) / MIB for row in continuous_daemon]
    continuous_private = [int(row["privateBytes"]) / MIB for row in continuous_daemon]

    lines = [
        "# Windows 后台架构性能基线",
        "",
        "> 测量方式：provider 层虚拟手柄 + 真实墙钟循环、引擎、IPC、三进程与 Windows `SendInput`。",
        "> 这不是物理手柄端到端验收，也不证明相对旧版本“手感更稳定”。",
        "",
        "## 结论摘要",
        "",
        f"- 无手柄且 GUI 退出时，daemon 平均 CPU 为 **{number(daemon_idle['cpuPercentOneCore'])}% 单核**，呈现宿主为 **{number(presenter_idle['cpuPercentOneCore'])}%**；两者合计工作集平均为 **{number(no_controller_total['workingAverage'])} MiB**，私有字节平均为 **{number(no_controller_total['privateAverage'])} MiB**。常驻内存主要在呈现宿主，而不是 daemon。",
        f"- 持续鼠标且 GUI 退出时，合计 CPU 为 **{number(mouse['totalCpuPercentOneCore'])}% 单核**，合计工作集平均为 **{number(mouse_total['workingAverage'])} MiB**；打开 GUI 后分别为 **{number(gui_open['totalCpuPercentOneCore'])}%** 和 **{number(gui_total['workingAverage'])} MiB**。GUI 打开成本明显，但 GUI 退出后其进程不常驻。",
        f"- 代码目标 Tick 间隔为 2 ms，但实测无手柄 Tick p50 为 **{percentile(no_controller['tickInterval'], 'p50Ms', 0.50)} ms**，持续鼠标为 **{percentile(mouse['tickInterval'], 'p50Ms', 0.50)} ms**，p95 为 **{percentile(mouse['tickInterval'], 'p95Ms', 0.95)} ms**。因此不能把目标间隔写成实际调度间隔。",
        f"- 本矩阵记录 **{success} 次成功 SendInput、0 次失败**。持续鼠标场景 Poll→首个 SendInput 返回的 p95 为 **{percentile(mouse['inputToInjection'], 'p95Ms', 0.95)} ms**；该指标不含物理输入送达与目标应用处理时间。",
        f"- 120 秒连续鼠标场景中，daemon 工作集从 **{number(continuous_working[0])}→{number(continuous_working[-1])} MiB**，私有字节从 **{number(continuous_private[0])}→{number(continuous_private[-1])} MiB**。这是需要延长观测和定位的增长信号，不能写成“无内存增长”。",
        "- 本报告是当前机器、当前工作区构建的一组可复跑基线，不是跨设备性能承诺。现有数据可以约束后续回归，但不足以宣称已证明“后台轻量”或“手感更稳定”。",
        "",
        "## 版本、机器与方法",
        "",
        "| 项目 | 记录 |",
        "|---|---|",
        f"| 版本 | `{metadata['productVersion']}` |",
        f"| 提交／工作区 | `{metadata['gitCommit']}`；`gitDirty={str(metadata['gitDirty']).lower()}`，含本轮测量代码 |",
        f"| 开始时间 | {started:%Y-%m-%d %H:%M:%S}（UTC+8） |",
        f"| 系统 | {metadata['windows']}，{metadata['machine']} |",
        f"| 处理器 | {metadata['processor']}；{metadata['logicalProcessors']} 个逻辑核心 |",
        f"| 构建 | Release；daemon 直接启动；`runnerElevated={str(metadata['runnerElevated']).lower()}` |",
        f"| 时长 | 普通场景 {metadata['durationSeconds']:g} 秒；连续场景 {metadata['longDurationSeconds']:g} 秒；GUI 开关 {metadata['guiCycles']} 轮 |",
        f"| 资源采样 | 每 {metadata['sampleIntervalSeconds']:g} 秒；预热后清零 Tick／延迟直方图 |",
        "",
        "虚拟输入位于 `IGamepadProvider`，不安装 HID 驱动。`mouse` 让右摇杆以 ±0.75 往返，持续产生真实鼠标注入；`macro` 持续长按 A，执行 `hold F23` 与 `every 50ms tap F24`；`multi` 使用四只错相虚拟手柄。所有场景使用隔离配置目录。",
        "",
        "`SessionKeyboardThroughputTests` 的模拟时间步与 `RecordingSink` 不在本测量路径中。所有进程启用指标采集，所以结果包含测量代码开销。daemon 不是计划任务拉起的提权实例。",
        "",
        "## 分角色资源占用",
        "",
        "CPU 的 100% 表示一个逻辑核心，不是整机利用率。内存列为“平均／采样峰值”，单位 MiB；工作集与私有字节是不同口径。分配速率使用各角色起止快照时间计算，GC 列为 Gen0／Gen1／Gen2 次数。",
        "",
        "| 场景 | 角色 | CPU（% 单核） | 工作集（MiB） | 私有字节（MiB） | 分配（MiB/s） | GC |",
        "|---|---|---:|---:|---:|---:|---:|",
    ]
    for name in order:
        scenario = by_name[name]
        for role in sorted(scenario["roles"], key=lambda item: ROLE_ORDER[item["role"]]):
            gc = f"{role['gen0']}／{role['gen1']}／{role['gen2']}"
            if name == "gui-toggle" and role["role"] == "gui":
                gc = "不完整"
            lines.append(
                f"| {LABELS[name]} | {ROLE_LABELS[role['role']]} | {number(role['cpuPercentOneCore'])} | "
                f"{memory_pair(role, 'workingSetAverageMB', 'workingSetPeakMB')} | "
                f"{memory_pair(role, 'privateAverageMB', 'privatePeakMB')} | "
                f"{allocation_rate(scenario, role['role'], scenarios_raw)} | {gc} |"
            )
    lines += [
        "",
        "GUI 开关场景的 GUI 内存均值只统计 GUI 进程仍存在的采样点；其分配与 GC 起始快照在打开约 4 秒后才取得，无法覆盖完整启动过程，故明确标为不完整。该场景通过创建后终止 GUI 进程施加压力，不是优雅退出验收。",
        "",
        "## 合计资源占用",
        "",
        "合计内存直接从 `resources.csv` 的同一 `sampleIndex` 求和后重新计算；不是把各角色在不同时间出现的峰值相加。GUI 开关场景把 GUI 已关闭的采样点计为 0。",
        "",
        "| 场景 | 墙钟（s） | 合计 CPU（% 单核） | 合计工作集平均／峰值（MiB） | 合计私有平均／峰值（MiB） |",
        "|---|---:|---:|---:|---:|",
    ]
    for name in order:
        scenario = by_name[name]
        total = totals[name]
        lines.append(
            f"| {LABELS[name]} | {number(scenario['wallSeconds'], 1)} | {number(scenario['totalCpuPercentOneCore'])} | "
            f"{number(total['workingAverage'])}／{number(total['workingPeak'])} | "
            f"{number(total['privateAverage'])}／{number(total['privatePeak'])} |"
        )
    lines += [
        "",
        "## Tick 与输入到注入延迟",
        "",
        "Tick 是相邻两次真实墙钟循环的间隔。输入到注入延迟从每次虚拟 provider `Poll` 开始，到该 Poll 对应的首个 `SendInput` 返回；一次 Poll 的后续注入不逐个计时。屏幕键盘持有输入租约，普通映射按设计暂停，所以没有注入样本。",
        "",
        "直方图分辨率 0.01 ms，线性范围 40.96 ms。`≥` 表示对应分位落入 overflow，只能给出下界；最大值始终为实测值。",
        "",
        "| 场景 | Tick p50／p95／p99／max（ms） | Tick 样本／overflow | 注入 p50／p95／p99／max（ms） | 注入样本／overflow | 成功／失败 SendInput |",
        "|---|---|---:|---|---:|---:|",
    ]
    for name in order:
        scenario = by_name[name]
        tick = timing_cells(scenario["tickInterval"])
        injected = timing_cells(scenario["inputToInjection"])
        lines.append(
            f"| {LABELS[name]} | {'／'.join(tick[:4])} | {(scenario['tickInterval'] or {}).get('count', 0)}／{tick[4]} | "
            f"{'／'.join(injected[:4])} | {(scenario['inputToInjection'] or {}).get('count', 0)}／{injected[4]} | "
            f"{scenario['injectionSuccessCount']}／{scenario['injectionFailureCount']} |"
        )

    continuous = by_name["mouse-continuous"]
    lines += [
        "",
        "## 120 秒连续运行观察",
        "",
        "下表是连续场景首末采样与最小二乘斜率。斜率只是这 120 秒窗口的描述，不能单独证明或排除内存泄漏。",
        "",
        "| 角色 | 工作集首→末（MiB） | 工作集斜率（MiB/min） | 私有首→末（MiB） | 私有斜率（MiB/min） |",
        "|---|---:|---:|---:|---:|",
    ]
    for role_name in ("daemon", "presentation"):
        role_rows = [row for row in rows if row["scenario"] == "mouse-continuous" and row["role"] == role_name]
        times = [(dt.datetime.fromisoformat(row["timestampUtc"]) - dt.datetime.fromisoformat(role_rows[0]["timestampUtc"])).total_seconds() / 60 for row in role_rows]
        working = [int(row["workingSetBytes"]) / MIB for row in role_rows]
        private = [int(row["privateBytes"]) / MIB for row in role_rows]
        working_slope = statistics.linear_regression(times, working).slope
        private_slope = statistics.linear_regression(times, private).slope
        lines.append(
            f"| {ROLE_LABELS[role_name]} | {number(working[0])}→{number(working[-1])} | {working_slope:+.3f} | "
            f"{number(private[0])}→{number(private[-1])} | {private_slope:+.3f} |"
        )
    lines += [
        "",
        f"连续场景 Tick p95 为 **{percentile(continuous['tickInterval'], 'p95Ms', 0.95)} ms**，注入延迟 p95 为 **{percentile(continuous['inputToInjection'], 'p95Ms', 0.95)} ms**，SendInput 成功／失败为 **{continuous['injectionSuccessCount']}／{continuous['injectionFailureCount']}**。",
        "",
        "## 验证与证据",
        "",
        "- 采集器已检查应有输入的场景存在成功 SendInput、所有场景没有失败 SendInput，并检查 GUI／浮层／屏幕键盘确实出现可见窗口。",
        "- 构建、自动化测试与进程清理结果见本次交付说明；本节不把未执行的真机项目标为通过。",
        f"- [运行元数据]({relative_link(data / 'metadata.json', output)})",
        f"- [场景汇总]({relative_link(data / 'summary.json', output)})",
        f"- [进程原始采样]({relative_link(data / 'resources.csv', output)})",
        f"- [起止指标与 GC]({relative_link(data / 'scenario-metrics.json', output)})",
        "- 同目录还保留每个进程的每秒指标和 stdout／stderr 日志。",
        "",
        "## 未覆盖边界",
        "",
        "1. 没有物理手柄，因此不测蓝牙／USB／XInput 输入送达、断连重连和休眠恢复。",
        "2. 延迟止于 `SendInput` 返回，不测目标应用接收、处理或画面呈现，不是端到端手感延迟。",
        "3. 不测普通应用与管理员应用的注入差异；daemon 与采集器同为非提权进程。",
        "4. 不做旧版本对照，因此不能证明“更稳定”；120 秒连续运行也不替代长时间挂机与泄漏验收。",
        "5. GUI 开关段使用进程终止，不覆盖用户点击关闭时的优雅清理；多屏、缩放、焦点与浮层位置仍属真机验收。",
        "",
        "## 复跑",
        "",
        "见 [虚拟手柄性能基线工具](../tools/PerfBaseline/README.md)。运行前关闭所有 DeziroJoy 进程；测试会持续移动鼠标并注入 F23／F24。",
        "",
    ]
    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("data", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.write_text(render(args.data, args.output), encoding="utf-8")


if __name__ == "__main__":
    main()
