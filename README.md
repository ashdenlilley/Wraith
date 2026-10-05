# RadeonLLM

Windows LLM runtime manager for AMD Radeon GPUs. Implements `RadeonLLM_Windows_Spec_v0.2.md`:
manages a pinned llama.cpp Vulkan `llama-server.exe` and exposes an OpenAI-compatible API at `http://127.0.0.1:8080/v1`.

## Build

```powershell
dotnet build                                  # all projects
dotnet test tests/RadeonLLM.Tests             # 35 tests (unit, API proxy, WPF smoke)
dotnet publish src/RadeonLLM.App -c Release -o publish   # self-contained single-file publish/RadeonLLM.exe
```

Requires the .NET 10 SDK (spec: .NET 8+).

Opt-in live tests (download the real runtime and use the GPU):

```powershell
$env:RADEONLLM_LIVE = 1
$env:RADEONLLM_TEST_GGUF = "C:\path\to\small-model.gguf"
dotnet test tests/RadeonLLM.Tests
```

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
