# RadeonLLM

Windows LLM runtime manager for AMD Radeon GPUs. Implements `RadeonLLM_Windows_Spec_v0.2.md`:
manages a pinned llama.cpp Vulkan `llama-server.exe` and exposes an OpenAI-compatible API at `http://127.0.0.1:8080/v1`.

## Build

```powershell
dotnet build                                  # all projects
dotnet test tests/RadeonLLM.Tests             # 52 tests (unit, API proxy, backend selection, WPF smoke)
dotnet publish src/RadeonLLM.App -c Release -o publish   # self-contained single-file publish/RadeonLLM.exe
```

Requires the .NET 10 SDK (spec: .NET 8+).

Opt-in live tests (download the real runtime and use the GPU):

```powershell
$env:RADEONLLM_LIVE = 1
$env:RADEONLLM_TEST_GGUF = "C:\path\to\small-model.gguf"
dotnet test tests/RadeonLLM.Tests
```

## Backends

Auto-selected from detected hardware (override in Settings, builds coexist on disk):

| Hardware | Auto picks | Fallback |
|---|---|---|
| NVIDIA, driver 525+ | CUDA (13.4 on driver 580+, else 12.4) | Vulkan, CPU |
| AMD / Intel / other GPU | Vulkan | CPU |
| No GPU | CPU | |

ROCm/HIP is opt-in. Every install is verified with `llama-server --list-devices`; if the backend sees no device
the app falls back (Auto) or restores the previous runtime (explicit choice).
Tested on AMD (Vulkan) and CPU. CUDA and ROCm success paths are untested: no NVIDIA/ROCm-capable hardware was available.

## Layout

| Project | Role |
|---|---|
| `RadeonLLM.Core` | settings, paths, rolling logs, shared contracts, error translation |
| `RadeonLLM.Hardware` | GPU/CPU/RAM detection, 1 s telemetry (LibreHardwareMonitor) |
| `RadeonLLM.Runtime` | llama.cpp install / update / rollback / repair / verify, `--list-devices` |
| `RadeonLLM.Models` | GGUF reader, model library, downloader, VRAM preflight |
| `RadeonLLM.Inference` | profile → llama-server args, process lifecycle, crash recovery |
| `RadeonLLM.Analytics` | SQLite sessions/requests, benchmark, stability test |
| `RadeonLLM.Api` | auth + analytics-observing reverse proxy in front of llama-server |
| `RadeonLLM.Security` | DPAPI-protected API key |
| `RadeonLLM.App` | WPF UI (Home, Models, Chat, Performance, Settings) |

Data lives in `%LOCALAPPDATA%\RadeonLLM\` (override with `RADEONLLM_HOME`).
