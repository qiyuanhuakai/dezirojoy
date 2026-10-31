# Memory footprint

Both platforms measured. RSS and private memory from `Process` counters after
the process settled; module sizes from `Process.Modules`.

## Windows (the platform that matters)

| Configuration | RSS | Private (commit) | Threads |
|---|---|---|---|
| Minimal Avalonia empty window | **175.8 MB** | **113.7 MB** | 64 |
| DeziroJoy Release | **252.1 MB** | **166.3 MB** | 58 |
| **DeziroJoy's own contribution** | **76.3 MB** | **52.6 MB** | — |
| DeziroJoy Debug (a dev build) | 295.2 MB | 207.8 MB | 59 |

The user reported ~160 MB, which matches the **private** column (166 MB), not
the RSS column. Windows Task Manager's memory column is a private-working-set
figure, which excludes pages shared with other processes; RSS counts them.
So 160 MB and 252 MB are both correct measurements of different things.

**Verdict: ~70 % of the footprint is the Avalonia/Skia/.NET floor. Only ~53 MB
private is the application.** A 60-second sample showed 0.8 MB growth and a
constant thread count — no leak.

### What the 176 MB floor actually is

The minimal empty window loads the identical module set as DeziroJoy. The
dominant entries are the GPU graphics stack:

| Module | Size | What it is |
|---|---|---|
| `igc64.dll` | **81.1 MB** | Intel Graphics Compiler — the GPU's shader compiler |
| `igd10umt64xe.DLL` | 26.0 MB | Intel D3D user-mode driver |
| `d3dcompiler_47.dll` | 5.5 MB | D3D shader compiler |
| `igd10iumd64.dll` | 5.3 MB | Intel D3D UMD |
| `igdgmm64.dll` | 5.6 MB | Intel graphics memory management |
| `av_libGLESv2.dll` | 5.2 MB | ANGLE's GLESv2 (Skia's GL backend) |
| `libSkiaSharp.DLL` | 11.1 MB | Skia |
| `System.Private.CoreLib.dll` | 15.3 MB | .NET runtime |
| `coreclr.dll` | 4.5 MB | .NET runtime |
| `icu.dll` | 2.7 MB | globalization data (split across DLLs on Windows) |

Graphics stack total: **~128.7 MB of mapped images**, of which most pages are
shared with every other process on the machine that touches the Intel GPU. This
is the Windows counterpart of the Linux finding below — on both platforms the
shader compiler dominates, and on both it is not our code.

## Linux (development box, no GPU)

| Configuration | RSS | Threads |
|---|---|---|
| Bare .NET 10 console | **28 MB** | 7 |
| Minimal Avalonia empty window | **145 MB** | 12 |
| DeziroJoy Release | **185 MB** | 12 |

The 145 MB floor here is inflated by a GPU-less environment: Skia falls back to
Mesa's **llvmpipe** software rasterizer, which loads `libLLVM.so` (**130 MB**)
plus `libgallium` (39 MB) and `libicudata.so` (29 MB). **None of that exists on
Windows**, so do not port this breakdown across. The Windows table above is the
one to read.

## What this means for the daemon

A UI-less daemon pays only the runtime floor, so it lands near **30–35 MB** on
either platform: a ~150 MB saving against the GUI. The window is the expensive
part, not the mapping.

## ICU, flagged not fixed

`<InvariantGlobalization>true</InvariantGlobalization>` would drop the
globalization data (29 MB on Linux, ~2.7 MB on Windows where it is split and
lazily loaded). Not applied: it is a global mode change with blast radius
beyond memory, and the saving is small on the platform that matters. Decide it
on its own merits, not as a memory optimization.

## How to re-measure

Windows (from WSL, via `\\wsl.localhost` interop):

```powershell
# framework floor reference: a minimal Avalonia window
Start-Process 'C:\path\to\MinimalAva\MinimalAva.exe'
# then, against either process:
dotnet MemProbe.dll --pid <pid> --duration 30
```

Linux:

```bash
dotnet build -c Release --nologo -v q
dotnet src/DeziroJoy.App/bin/Release/net10.0/DeziroJoy.dll &
sleep 6; ps -o rss=,nlwp= -p $!
```

Always compare against the minimal-Avalonia reference before concluding that a
number is the application's fault; the software-rasterizer and GPU-driver
floors are environment-specific and large.
