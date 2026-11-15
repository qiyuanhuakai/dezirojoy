#!/usr/bin/env python3
"""DeziroJoy provider-layer virtual-pad wall-clock performance baseline.

No third-party packages or virtual HID driver are used. The runner starts the real daemon,
presentation host and GUI with isolated APPDATA, samples Windows process resources through
ctypes, and preserves raw CSV/JSON evidence in the workspace.
"""

from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
import csv
import datetime as dt
import json
import os
from pathlib import Path
import platform
import statistics
import subprocess
import sys
import time
from typing import Any

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
CREATE_NO_WINDOW = 0x08000000
MB = 1024 * 1024

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)
user32 = ctypes.WinDLL("user32", use_last_error=True)


class FILETIME(ctypes.Structure):
    _fields_ = [("dwLowDateTime", wintypes.DWORD), ("dwHighDateTime", wintypes.DWORD)]


class PROCESS_MEMORY_COUNTERS_EX(ctypes.Structure):
    _fields_ = [
        ("cb", wintypes.DWORD),
        ("PageFaultCount", wintypes.DWORD),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
        ("PrivateUsage", ctypes.c_size_t),
    ]


kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel32.OpenProcess.restype = wintypes.HANDLE
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.GetProcessTimes.argtypes = [
    wintypes.HANDLE,
    ctypes.POINTER(FILETIME),
    ctypes.POINTER(FILETIME),
    ctypes.POINTER(FILETIME),
    ctypes.POINTER(FILETIME),
]
psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESS_MEMORY_COUNTERS_EX), wintypes.DWORD]


def filetime_seconds(value: FILETIME) -> float:
    return ((value.dwHighDateTime << 32) | value.dwLowDateTime) / 10_000_000


def process_sample(pid: int) -> tuple[float, int, int]:
    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return 0.0, 0, 0
    try:
        creation, exit_time, kernel, user = FILETIME(), FILETIME(), FILETIME(), FILETIME()
        if not kernel32.GetProcessTimes(handle, ctypes.byref(creation), ctypes.byref(exit_time), ctypes.byref(kernel), ctypes.byref(user)):
            return 0.0, 0, 0
        memory = PROCESS_MEMORY_COUNTERS_EX()
        memory.cb = ctypes.sizeof(memory)
        if not psapi.GetProcessMemoryInfo(handle, ctypes.byref(memory), memory.cb):
            return 0.0, 0, 0
        return filetime_seconds(kernel) + filetime_seconds(user), int(memory.WorkingSetSize), int(memory.PrivateUsage)
    finally:
        kernel32.CloseHandle(handle)


def visible_window_count(pid: int) -> int:
    count = 0
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def visit(hwnd: int, _: int) -> bool:
        nonlocal count
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            count += 1
        return True

    callback = callback_type(visit)
    user32.EnumWindows(callback, 0)
    return count


def cursor_position() -> tuple[int, int]:
    point = wintypes.POINT()
    if user32.GetCursorPos(ctypes.byref(point)):
        return point.x, point.y
    return 0, 0


def restore_cursor(position: tuple[int, int]) -> None:
    if position != (0, 0):
        user32.SetCursorPos(*position)


class Baseline:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.root = Path(args.root).resolve()
        self.app = Path(args.app).resolve()
        stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
        self.output = Path(args.output).resolve() if args.output else self.root / "artifacts" / "performance" / stamp
        self.output.mkdir(parents=True, exist_ok=False)
        self.rows: list[dict[str, Any]] = []
        self.scenarios: list[dict[str, Any]] = []
        self.active: list[tuple[str, subprocess.Popen[Any]]] = []
        self.sample_index = 0
        self.cursor = cursor_position()

    def environment(self, mode: str, overlay: bool) -> dict[str, str]:
        env = os.environ.copy()
        sandbox = self.output / "appdata"
        sandbox.mkdir(exist_ok=True)
        for name in ("APPDATA", "LOCALAPPDATA", "TEMP", "TMP"):
            env[name] = str(sandbox)
        env["DEZIROJOY_PERF_DIR"] = str(self.output)
        env["DEZIROJOY_PERF_METRICS"] = "1"
        env["DEZIROJOY_VIRTUAL_PAD"] = mode
        env["DEZIROJOY_PERF_OVERLAY"] = "1" if overlay else "0"
        return env

    def launch(self, role: str, mode: str, overlay: bool, arguments: list[str]) -> subprocess.Popen[Any]:
        stdout = (self.output / f"{role}-{mode}-{int(time.time() * 1000)}.out.log").open("w", encoding="utf-8")
        stderr = (self.output / f"{role}-{mode}-{int(time.time() * 1000)}.err.log").open("w", encoding="utf-8")
        process = subprocess.Popen(
            [str(self.app), *arguments],
            cwd=self.root,
            env=self.environment(mode, overlay),
            stdout=stdout,
            stderr=stderr,
            creationflags=CREATE_NO_WINDOW if role != "gui" else 0,
        )
        process._baseline_streams = (stdout, stderr)  # type: ignore[attr-defined]
        self.last_mode = mode
        self.last_overlay = overlay
        self.active.append((role, process))
        return process

    def control(self, command: str, mode: str, overlay: bool = False) -> None:
        result = subprocess.run(
            [str(self.app), "--perf-control", command],
            cwd=self.root,
            env=self.environment(mode, overlay),
            capture_output=True,
            text=True,
            timeout=20,
            creationflags=CREATE_NO_WINDOW,
        )
        if result.returncode != 0:
            raise RuntimeError(f"perf-control {command} failed: {result.stdout} {result.stderr}")

    def wait_metrics(self, role: str, process: subprocess.Popen[Any], timeout: float = 15) -> Path:
        path = self.output / f"metrics-{role}-{process.pid}.json"
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if path.exists():
                return path
            if process.poll() is not None:
                raise RuntimeError(f"{role} exited with {process.returncode}; see {self.output} logs")
            time.sleep(0.1)
        raise TimeoutError(f"metrics not written: {path}")

    def request_reset(self, processes: list[tuple[str, subprocess.Popen[Any]]]) -> None:
        requests = [self.output / f"reset-{process.pid}.request" for _, process in processes]
        for path in requests:
            path.write_text("reset", encoding="ascii")
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline and any(path.exists() for path in requests):
            exited = [(role, process.pid, process.returncode) for role, process in processes if process.poll() is not None]
            if exited:
                pending = [str(path.name) for path in requests if path.exists()]
                raise RuntimeError(f"process exited during performance reset: {exited}; pending={pending}; see {self.output} logs")
            time.sleep(0.1)
        if any(path.exists() for path in requests):
            pending = [str(path.name) for path in requests if path.exists()]
            states = [(role, process.pid, process.poll()) for role, process in processes]
            raise TimeoutError(f"performance reset was not acknowledged: pending={pending}; processes={states}")
        time.sleep(1.2)

    @staticmethod
    def read_metrics(path: Path) -> dict[str, Any]:
        with path.open("r", encoding="utf-8-sig") as stream:
            return json.load(stream)

    def capture(self, scenario: str, processes: list[tuple[str, subprocess.Popen[Any]]]) -> None:
        self.sample_index += 1
        timestamp = dt.datetime.now(dt.timezone.utc).isoformat()
        for role, process in processes:
            if process.poll() is not None:
                continue
            cpu, working_set, private = process_sample(process.pid)
            self.rows.append({
                "sampleIndex": self.sample_index,
                "timestampUtc": timestamp,
                "scenario": scenario,
                "role": role,
                "pid": process.pid,
                "cpuTotalSeconds": cpu,
                "workingSetBytes": working_set,
                "privateBytes": private,
                "visibleWindows": visible_window_count(process.pid),
            })

    def measure(self, name: str, seconds: float, processes: list[tuple[str, subprocess.Popen[Any]]]) -> dict[str, Any]:
        self.request_reset(processes)
        started = time.monotonic()
        started_utc = dt.datetime.now(dt.timezone.utc).isoformat()
        metrics_start = {
            process.pid: self.read_metrics(self.output / f"metrics-{role}-{process.pid}.json")
            for role, process in processes if process.poll() is None
        }
        deadline = started + seconds
        while time.monotonic() < deadline:
            self.capture(name, processes)
            time.sleep(self.args.sample_interval)
        ended = time.monotonic()
        # Ask reporters for one last snapshot before reading cumulative engine metrics.
        time.sleep(1.2)
        metrics_end = {
            process.pid: self.read_metrics(self.output / f"metrics-{role}-{process.pid}.json")
            for role, process in processes if process.poll() is None
        }
        scenario = {
            "name": name,
            "startedUtc": started_utc,
            "wallSeconds": ended - started,
            "pids": {role: process.pid for role, process in processes},
        }
        self.scenarios.append({**scenario, "metricsStart": metrics_start, "metricsEnd": metrics_end})
        print(f"[done] {name}: {ended - started:.1f}s", flush=True)
        return scenario

    def stop_gui(self, gui: subprocess.Popen[Any]) -> None:
        if gui.poll() is None:
            gui.terminate()
            try:
                gui.wait(timeout=10)
            except subprocess.TimeoutExpired:
                gui.kill()
                gui.wait(timeout=5)
        self.active = [(role, process) for role, process in self.active if process.pid != gui.pid]

    def run_standard(
        self,
        name: str,
        mode: str,
        *,
        gui: bool = False,
        overlay: bool = False,
        keyboard: bool = False,
        seconds: float | None = None,
    ) -> None:
        daemon = self.launch("daemon", mode, overlay, ["--daemon"])
        self.wait_metrics("daemon", daemon)
        self.control("start", mode, overlay)
        presentation = self.launch("presentation", mode, overlay, ["--tray"])
        self.wait_metrics("presentation", presentation)
        processes = [("daemon", daemon), ("presentation", presentation)]
        gui_process = None
        if gui:
            gui_process = self.launch("gui", mode, overlay, [])
            self.wait_metrics("gui", gui_process)
            processes.append(("gui", gui_process))
        if keyboard:
            self.control("keyboard-open", mode, overlay)
        time.sleep(self.args.warmup_seconds)
        self.measure(name, seconds or self.args.duration, processes)
        if gui_process is not None:
            self.stop_gui(gui_process)
        self.control("keyboard-close", mode, overlay) if keyboard else None
        self.control("shutdown", mode, overlay)
        daemon.wait(timeout=10)
        self.terminate(presentation)
        restore_cursor(self.cursor)
        time.sleep(0.5)

    def run_gui_toggle(self) -> None:
        mode = "mouse"
        daemon = self.launch("daemon", mode, False, ["--daemon"])
        self.wait_metrics("daemon", daemon)
        self.control("start", mode)
        presentation = self.launch("presentation", mode, False, ["--tray"])
        self.wait_metrics("presentation", presentation)
        processes = [("daemon", daemon), ("presentation", presentation)]
        self.request_reset(processes)
        name = "gui-toggle"
        started = time.monotonic()
        started_utc = dt.datetime.now(dt.timezone.utc).isoformat()
        metrics_start = {
            process.pid: self.read_metrics(self.output / f"metrics-{role}-{process.pid}.json")
            for role, process in processes
        }
        gui_metrics_start: dict[int, Any] = {}
        for _ in range(self.args.gui_cycles):
            gui = self.launch("gui", mode, False, [])
            self.wait_metrics("gui", gui)
            processes.append(("gui", gui))
            self.capture(name, processes)
            time.sleep(self.args.gui_open_seconds)
            gui_metrics_start[gui.pid] = self.read_metrics(self.output / f"metrics-gui-{gui.pid}.json")
            self.capture(name, processes)
            self.stop_gui(gui)
            processes = [entry for entry in processes if entry[1].pid != gui.pid]
            time.sleep(self.args.gui_closed_seconds)
            self.capture(name, processes)
        ended = time.monotonic()
        time.sleep(1.2)
        metrics_end = {
            process.pid: self.read_metrics(self.output / f"metrics-{role}-{process.pid}.json")
            for role, process in processes
        }
        gui_metrics_end = {
            pid: self.read_metrics(self.output / f"metrics-gui-{pid}.json")
            for pid in gui_metrics_start
        }
        self.scenarios.append({
            "name": name,
            "startedUtc": started_utc,
            "wallSeconds": ended - started,
            "pids": {role: process.pid for role, process in processes},
            "metricsStart": {**metrics_start, **gui_metrics_start},
            "metricsEnd": {**metrics_end, **gui_metrics_end},
        })
        self.control("shutdown", mode)
        daemon.wait(timeout=10)
        self.terminate(presentation)
        restore_cursor(self.cursor)
        print(f"[done] {name}: {ended - started:.1f}s", flush=True)

    @staticmethod
    def terminate(process: subprocess.Popen[Any]) -> None:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)

    def cleanup(self) -> None:
        # A failed directly launched daemon may have been replaced by the control client's
        # elevated launch. Stop that untracked child through IPC without ever launching a new
        # daemon merely to clean up; then terminate the processes this runner owns directly.
        try:
            self.control("shutdown", getattr(self, "last_mode", "none"), getattr(self, "last_overlay", False))
        except Exception:
            pass
        for _, process in reversed(self.active):
            self.terminate(process)
        self.active.clear()
        restore_cursor(self.cursor)

    def write_raw(self) -> None:
        with (self.output / "resources.csv").open("w", newline="", encoding="utf-8-sig") as stream:
            writer = csv.DictWriter(stream, fieldnames=list(self.rows[0].keys()) if self.rows else ["scenario"])
            writer.writeheader()
            writer.writerows(self.rows)
        with (self.output / "scenario-metrics.json").open("w", encoding="utf-8") as stream:
            json.dump(self.scenarios, stream, ensure_ascii=False, indent=2)

    def summarize(self) -> list[dict[str, Any]]:
        summaries = []
        for scenario in self.scenarios:
            rows = [row for row in self.rows if row["scenario"] == scenario["name"]]
            roles = []
            for role in sorted({row["role"] for row in rows}):
                role_rows = [row for row in rows if row["role"] == role]
                pids = sorted({row["pid"] for row in role_rows})
                cpu_seconds = 0.0
                allocated = 0
                gen = [0, 0, 0]
                for pid in pids:
                    pid_rows = sorted((row for row in role_rows if row["pid"] == pid), key=lambda row: row["sampleIndex"])
                    if len(pid_rows) >= 2:
                        cpu_seconds += max(0.0, pid_rows[-1]["cpuTotalSeconds"] - pid_rows[0]["cpuTotalSeconds"])
                    start = scenario["metricsStart"].get(str(pid), scenario["metricsStart"].get(pid))
                    end = scenario["metricsEnd"].get(str(pid), scenario["metricsEnd"].get(pid))
                    if start and end:
                        allocated += max(0, end["totalAllocatedBytes"] - start["totalAllocatedBytes"])
                        for index in range(3):
                            gen[index] += max(0, end[f"gen{index}Collections"] - start[f"gen{index}Collections"])
                by_sample: dict[int, list[dict[str, Any]]] = {}
                for row in role_rows:
                    by_sample.setdefault(row["sampleIndex"], []).append(row)
                working = [sum(row["workingSetBytes"] for row in group) for group in by_sample.values()]
                private = [sum(row["privateBytes"] for row in group) for group in by_sample.values()]
                roles.append({
                    "role": role,
                    "pids": pids,
                    "cpuPercentOneCore": cpu_seconds / scenario["wallSeconds"] * 100 if scenario["wallSeconds"] else 0,
                    "workingSetAverageMB": statistics.fmean(working) / MB if working else 0,
                    "workingSetPeakMB": max(working, default=0) / MB,
                    "privateAverageMB": statistics.fmean(private) / MB if private else 0,
                    "privatePeakMB": max(private, default=0) / MB,
                    "allocatedMB": allocated / MB,
                    "allocatedMBPerSecond": allocated / MB / scenario["wallSeconds"],
                    "gen0": gen[0],
                    "gen1": gen[1],
                    "gen2": gen[2],
                    "maxVisibleWindows": max((row["visibleWindows"] for row in role_rows), default=0),
                })
            daemon_metrics = next((value for key, value in scenario["metricsEnd"].items()
                                   if value.get("role") == "daemon"), None)
            summaries.append({
                "name": scenario["name"],
                "wallSeconds": scenario["wallSeconds"],
                "roles": roles,
                "totalCpuPercentOneCore": sum(role["cpuPercentOneCore"] for role in roles),
                "totalWorkingSetAverageMB": sum(role["workingSetAverageMB"] for role in roles),
                "totalWorkingSetPeakMB": sum(role["workingSetPeakMB"] for role in roles),
                "totalPrivateAverageMB": sum(role["privateAverageMB"] for role in roles),
                "totalPrivatePeakMB": sum(role["privatePeakMB"] for role in roles),
                "totalAllocatedMB": sum(role["allocatedMB"] for role in roles),
                "injectionSuccessCount": daemon_metrics.get("injectionSuccessCount", 0) if daemon_metrics else 0,
                "injectionFailureCount": daemon_metrics.get("injectionFailureCount", 0) if daemon_metrics else 0,
                "tickInterval": daemon_metrics["tickInterval"] if daemon_metrics else None,
                "inputToInjection": daemon_metrics["inputToInjection"] if daemon_metrics else None,
            })
        with (self.output / "summary.json").open("w", encoding="utf-8") as stream:
            json.dump(summaries, stream, ensure_ascii=False, indent=2)
        return summaries

    @staticmethod
    def validate(summaries: list[dict[str, Any]]) -> None:
        for scenario in summaries:
            name = scenario["name"]
            if scenario["injectionFailureCount"]:
                raise RuntimeError(f"{name}: SendInput failed {scenario['injectionFailureCount']} times; baseline is not a passing run")
            if name != "mouse-screen-keyboard" and name.split("-", 1)[0] in ("mouse", "macro", "multi"):
                if scenario["injectionSuccessCount"] <= 0:
                    raise RuntimeError(f"{name}: no successful SendInput evidence")
            required_role = "gui" if name in ("mouse-gui-open", "gui-toggle") else "presentation" if name in ("mouse-overlay", "mouse-screen-keyboard") else None
            if required_role:
                role = next((role for role in scenario["roles"] if role["role"] == required_role), None)
                if role is None or role["maxVisibleWindows"] < 1:
                    raise RuntimeError(f"{name}: {required_role} had no observed visible window")

    def run(self) -> None:
        existing = subprocess.run(
            ["powershell.exe", "-NoProfile", "-Command", "@(Get-Process DeziroJoy -ErrorAction SilentlyContinue).Count"],
            capture_output=True, text=True, timeout=15,
        ).stdout.strip()
        if existing not in ("", "0"):
            raise RuntimeError("Close every DeziroJoy process before the baseline; the runner never kills a pre-existing instance.")
        metadata = {
            "startedUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
            "app": str(self.app),
            "productVersion": subprocess.run(
                ["powershell.exe", "-NoProfile", "-Command", f"(Get-Item '{self.app}').VersionInfo.ProductVersion"],
                capture_output=True, text=True, timeout=15,
            ).stdout.strip(),
            "gitCommit": subprocess.run(["git", "rev-parse", "HEAD"], cwd=self.root, capture_output=True, text=True).stdout.strip(),
            "gitDirty": bool(subprocess.run(["git", "status", "--porcelain"], cwd=self.root, capture_output=True, text=True).stdout.strip()),
            "windows": platform.platform(),
            "machine": platform.machine(),
            "processor": subprocess.run(
                ["powershell.exe", "-NoProfile", "-Command", "(Get-CimInstance Win32_Processor).Name"],
                capture_output=True, text=True, timeout=15).stdout.strip(),
            "logicalProcessors": os.cpu_count(),
            "runnerElevated": bool(ctypes.windll.shell32.IsUserAnAdmin()),
            "daemonLaunch": "Direct child of the runner, with the same elevation as the runner; not a scheduled-task instance.",
            "python": sys.version,
            "sampleIntervalSeconds": self.args.sample_interval,
            "durationSeconds": self.args.duration,
            "longDurationSeconds": self.args.long_duration,
            "guiCycles": self.args.gui_cycles,
            "boundary": "Provider-layer virtual pad; real engine/IPC/SendInput/wall clock. No Bluetooth/XInput/HID delivery or input-to-target-app latency claim.",
        }
        with (self.output / "metadata.json").open("w", encoding="utf-8") as stream:
            json.dump(metadata, stream, ensure_ascii=False, indent=2)
        print(f"Output: {self.output}", flush=True)
        try:
            self.run_standard("no-controller-gui-exited", "none")
            self.run_standard("idle-gui-exited", "idle")
            self.run_standard("mouse-gui-exited", "mouse")
            self.run_standard("macro-gui-exited", "macro")
            self.run_standard("multi-gui-exited", "multi")
            self.run_standard("mouse-gui-open", "mouse", gui=True)
            self.run_standard("mouse-overlay", "mouse", overlay=True)
            self.run_standard("mouse-screen-keyboard", "mouse", keyboard=True)
            self.run_standard("mouse-continuous", "mouse", seconds=self.args.long_duration)
            self.run_gui_toggle()
        finally:
            self.cleanup()
            self.write_raw()
            summaries = self.summarize()
            print(json.dumps(summaries, ensure_ascii=False, indent=2), flush=True)
        self.validate(summaries)
        print("Validation: successful injections and required visible windows confirmed; no SendInput failures.", flush=True)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=str(Path(__file__).resolve().parents[2]))
    parser.add_argument("--app", default=str(Path(__file__).resolve().parents[2] / "src" / "DeziroJoy.App" / "bin" / "Release" / "net10.0" / "DeziroJoy.exe"))
    parser.add_argument("--output")
    parser.add_argument("--duration", type=float, default=20.0)
    parser.add_argument("--long-duration", type=float, default=120.0)
    parser.add_argument("--warmup-seconds", type=float, default=4.0)
    parser.add_argument("--sample-interval", type=float, default=0.25)
    parser.add_argument("--gui-cycles", type=int, default=6)
    parser.add_argument("--gui-open-seconds", type=float, default=4.0)
    parser.add_argument("--gui-closed-seconds", type=float, default=2.0)
    args = parser.parse_args()
    if args.duration < 5 or args.long_duration < args.duration:
        parser.error("duration must be >=5 and long-duration >= duration")
    Baseline(args).run()


if __name__ == "__main__":
    main()
