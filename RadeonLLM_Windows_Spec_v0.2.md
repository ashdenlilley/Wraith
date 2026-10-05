# RadeonLLM --- Windows AMD LLM Runtime

## Product & Technical Specification

**Version:** 0.2\
**Target:** Windows 11 x64\
**Primary GPU:** AMD Radeon RX 6900 XT 16 GB\
**Primary backend:** Vulkan\
**Inference engine:** llama.cpp / `llama-server.exe`\
**Primary interface:** OpenAI-compatible local HTTP API\
**Distribution:** Self-contained Windows executable

------------------------------------------------------------------------

# 1. Product Objective

Create an extremely simple Windows `.exe` that turns a Windows PC with a
supported GPU---initially optimised for the AMD Radeon RX 6900 XT 16
GB---into a local LLM workstation.

The application is fundamentally a:

> **Local OpenAI-compatible inference server + GPU/runtime manager +
> performance analytics utility.**

It should not attempt to become a full AI platform.

The application must:

1.  Detect GPU and system hardware.
2.  Verify required Windows components and drivers.
3.  Install and manage a validated llama.cpp Vulkan runtime.
4.  Download/import/manage GGUF models.
5.  Automatically configure inference for the available GPU/VRAM.
6.  Launch and manage `llama-server.exe`.
7.  Expose an **OpenAI-compatible HTTP API** on localhost.
8.  Provide an optional minimal local chat UI.
9.  Collect token, latency and hardware performance metrics.
10. Provide benchmark and stability-testing functionality.
11. Store analytics locally.
12. Require essentially zero configuration for normal use.

------------------------------------------------------------------------

# 2. Core Product Philosophy

### Primary principle

**Install once. Run simply. Integrate anywhere.**

Ideal flow:

``` text
RadeonLLM.exe
      ↓
Detect hardware
      ↓
Verify Vulkan
      ↓
Install validated llama.cpp runtime
      ↓
Import/select GGUF model
      ↓
Automatic configuration
      ↓
Start inference server
      ↓
OpenAI-compatible API
      ↓
Any compatible client
```

The user should not need to understand:

-   ROCm
-   HIP
-   Vulkan internals
-   gfx1030
-   CMake
-   Python environments
-   CUDA
-   Docker
-   WSL
-   llama.cpp command-line arguments

------------------------------------------------------------------------

# 3. Primary Use Case

The primary use case is a **dedicated Windows AI/workstation GPU**.

Example hardware:

``` text
Windows 11 workstation
        │
        ├── AMD RX 6900 XT 16 GB
        │
        ├── Vulkan
        │
        └── RadeonLLM
                │
                ├── llama-server
                │
                └── OpenAI-compatible API
                         │
              ┌──────────┼──────────┐
              │          │          │
             IDE       Scripts   Custom Apps
```

The application should allow other Windows applications to use the GPU
without needing to know anything about the underlying llama.cpp/Vulkan
implementation.

------------------------------------------------------------------------

# 4. Target Platform

Initial target:

``` text
Windows 11 x64
AMD Radeon GPU
Vulkan-capable AMD driver
```

Primary development/test GPU:

``` text
Sapphire RX 6900 XT Toxic Extreme
16 GB VRAM
RDNA2 / gfx1030
```

Future hardware should be supportable without redesigning the
application.

Potential future targets:

-   RX 6000
-   RX 7000
-   RX 9000
-   NVIDIA RTX
-   Intel Arc

The initial implementation remains:

> **AMD + Vulkan first.**

------------------------------------------------------------------------

# 5. High-Level Architecture

``` text
┌──────────────────────────────────────────────┐
│                RadeonLLM.exe                 │
│                                              │
│  ┌────────────┐  ┌────────────────────────┐  │
│  │ Minimal UI │  │ OpenAI API Controller  │  │
│  └─────┬──────┘  └────────────┬───────────┘  │
│        │                      │              │
│        └──────────┬───────────┘              │
│                   ▼                          │
│          Inference Manager                  │
│                   │                          │
│          Runtime Manager                    │
│                   │                          │
│          Model Manager                      │
│                   │                          │
│          Analytics Engine                   │
│                   │                          │
│          Hardware Monitor                   │
└───────────────────┬──────────────────────────┘
                    │
                    ▼
             llama-server.exe
                    │
                    ▼
                 Vulkan
                    │
                    ▼
             AMD Radeon GPU
```

The first version should **not embed llama.cpp directly**.

RadeonLLM manages `llama-server.exe` as an external child process.

This keeps the application lightweight and allows llama.cpp to be
upgraded independently.

------------------------------------------------------------------------

# 6. OpenAI-Compatible API --- Core Requirement

The local API is a **first-class feature**, not an optional add-on.

Default endpoint:

``` text
http://127.0.0.1:8080/v1/
```

The application should expose an OpenAI-compatible interface through the
managed llama.cpp server.

Minimum required endpoints:

``` text
GET  /v1/models

POST /v1/chat/completions

POST /v1/completions

POST /v1/embeddings
```

Where supported by the active llama.cpp runtime.

------------------------------------------------------------------------

# 7. Chat Completions

Example request:

``` http
POST http://127.0.0.1:8080/v1/chat/completions
Content-Type: application/json
```

``` json
{
  "model": "local-model",
  "messages": [
    {
      "role": "user",
      "content": "Write a C# function to parse this JSON."
    }
  ],
  "temperature": 0.7,
  "max_tokens": 2048
}
```

Response should follow the OpenAI-compatible structure provided by the
underlying llama.cpp server.

Example:

``` json
{
  "id": "chatcmpl-local-123",
  "object": "chat.completion",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "..."
      },
      "finish_reason": "stop"
    }
  ],
  "usage": {
    "prompt_tokens": 42,
    "completion_tokens": 128,
    "total_tokens": 170
  }
}
```

RadeonLLM should not unnecessarily transform the standard llama.cpp API
response.

------------------------------------------------------------------------

# 8. Streaming

Streaming must be supported.

Example:

``` json
{
  "model": "local-model",
  "messages": [
    {
      "role": "user",
      "content": "Explain Vulkan."
    }
  ],
  "stream": true
}
```

The API should support the standard server-sent streaming behaviour
expected by OpenAI-compatible clients.

------------------------------------------------------------------------

# 9. API Authentication

Because the server is local by default:

``` text
127.0.0.1 only
```

Authentication should be optional.

Default:

``` text
Authentication: Disabled
Bind address: 127.0.0.1
```

If the user enables LAN access:

``` text
Authentication: Required
```

RadeonLLM should generate a local API key.

Never expose an unauthenticated inference server to the LAN by default.

------------------------------------------------------------------------

# 10. LAN Access

Optional setting:

``` text
Network Access

○ Localhost only
● Local network
```

When LAN access is enabled:

``` text
Bind:
0.0.0.0

Authentication:
Required
```

Display the endpoint:

``` text
http://192.168.x.x:8080/v1/
```

The application must clearly warn the user before enabling network
exposure.

------------------------------------------------------------------------

# 11. Model Manager

Primary model format:

``` text
GGUF
```

Supported operations:

-   Add model
-   Import model
-   Remove model
-   Rename model
-   Download model
-   Verify model
-   Set default model
-   Inspect model metadata

Model cards should show:

``` text
Qwen 14B
Q5_K_M

Size
~10 GB

Estimated VRAM
~11 GB

RX 6900 XT
✓ Recommended
```

------------------------------------------------------------------------

# 12. Model Selection and API Model Names

Each installed model receives a stable API identifier.

Example:

``` text
qwen2.5-coder-14b-q5km
```

API request:

``` json
{
  "model": "qwen2.5-coder-14b-q5km"
}
```

RadeonLLM maps this logical model name to the corresponding GGUF file
and runtime configuration.

The user should not need to provide filesystem paths through the API.

------------------------------------------------------------------------

# 13. Model Lifecycle

Only one model needs to be actively loaded for MVP.

Lifecycle:

``` text
Model installed
      ↓
Model selected
      ↓
VRAM preflight
      ↓
Runtime configuration
      ↓
llama-server launch
      ↓
Health check
      ↓
API available
```

Future versions may support:

-   Multiple loaded models
-   Model hot swapping
-   Model pools
-   Automatic model selection

These are not required for MVP.

------------------------------------------------------------------------

# 14. VRAM Preflight

Before starting a model, estimate:

``` text
Model weights
+
KV cache
+
Runtime overhead
+
Temporary buffers
```

Example:

``` text
Model              10.8 GB
KV cache             1.8 GB
Runtime              0.7 GB
---------------------------
Estimated total     13.3 GB

Available            15.4 GB

STATUS: ✓ SAFE
```

If unsafe:

``` text
Estimated total
17.2 GB

Available
15.4 GB

STATUS: ⚠ VRAM LIMITED

Recommended:
Context: 8K → 4K

[Apply Automatically]
```

The application should avoid launching configurations that are highly
likely to exhaust VRAM.

------------------------------------------------------------------------

# 15. Automatic Inference Configuration

The user should not need to configure raw llama.cpp flags.

Provide:

``` text
Performance

○ Safe
● Balanced
○ Maximum
○ Custom
```

Profiles control:

-   GPU layer offload
-   Context size
-   Batch size
-   Flash Attention
-   KV cache configuration
-   CPU threads
-   GPU backend
-   CPU/GPU hybrid inference

------------------------------------------------------------------------

# 16. Default RX 6900 XT Profile

Starting configuration:

``` text
Backend
Vulkan

GPU offload
Maximum

Context
16K

Flash Attention
Enabled where supported

CPU threads
Automatic

Batch
Automatic

GPU layers
Automatic maximum

KV cache
Automatic
```

If VRAM becomes constrained, progressively:

1.  Reduce context.
2.  Reduce batch size.
3.  Reduce GPU layers.
4.  Enable additional CPU/GPU hybrid inference.

------------------------------------------------------------------------

# 17. Runtime Manager

The Runtime Manager owns the llama.cpp installation.

Supported operations:

``` text
Install
Update
Rollback
Repair
Remove
Verify
```

Runtime versions should be pinned.

Example:

``` json
{
  "runtime": "llama.cpp",
  "version": "YYYY-MM-DD",
  "backend": "Vulkan",
  "architecture": "x64"
}
```

Do not automatically update the runtime immediately after every upstream
release.

Instead:

``` text
New runtime available

Current: X
Available: Y

[Update] [Ignore]
```

------------------------------------------------------------------------

# 18. llama-server Process Management

RadeonLLM owns the complete lifecycle of `llama-server.exe`.

Responsibilities:

-   Start
-   Stop
-   Restart
-   Monitor
-   Capture stdout/stderr
-   Detect crashes
-   Detect failed startup
-   Detect GPU/runtime errors
-   Verify API health
-   Restart after recoverable failure

Health check:

``` text
GET /v1/models
```

A server is considered ready only after the health check succeeds.

------------------------------------------------------------------------

# 19. Main UI

Keep the initial application to approximately four screens.

## Home

``` text
┌───────────────────────────────────────┐
│ RadeonLLM                             │
│                                       │
│ RX 6900 XT          16 GB             │
│ Vulkan              ✓                 │
│                                       │
│ Model                                 │
│ Qwen 14B Q5_K_M                       │
│                                       │
│ API                                   │
│ ● Running  127.0.0.1:8080            │
│                                       │
│              [ RUN ]                  │
│                                       │
│  GPU       97%                        │
│  VRAM      11.2 / 16 GB               │
│  Speed     48.2 tok/s                 │
└───────────────────────────────────────┘
```

## Models

``` text
Installed Models

Qwen 14B Q5_K_M
10.8 GB
Recommended

DeepSeek 14B Q4
9.1 GB

[Add Model]
[Download Model]
```

## Performance

``` text
Current Session

Prompt tokens       1,240
Generated tokens    8,420

Prompt processing   1,850 tok/s
Generation          47.8 tok/s

GPU utilisation     98%
VRAM                11.4 GB
GPU power           285 W
GPU temperature     61°C
Hotspot             72°C

Context             12.3K / 16K

Session duration    18:42
```

## Settings

``` text
Backend
Vulkan

Context
16K

GPU Offload
Maximum

Performance Profile
Balanced

API Server
Enabled

Bind Address
127.0.0.1

Port
8080

API Key
[••••••••]
```

------------------------------------------------------------------------

# 20. Minimal Built-In Chat

The built-in chat exists primarily as a diagnostic and convenience
interface.

It must use the same OpenAI-compatible API exposed to external
applications.

Architecture:

``` text
Built-in Chat
      │
      ▼
localhost /v1/chat/completions
      │
      ▼
llama-server
```

Do not create a separate inference path for the UI.

This ensures that:

> If the API works, the built-in chat works.

------------------------------------------------------------------------

# 21. Token Analytics

Every inference request/session should record:

### Generation

-   Generated tokens
-   Generation time
-   Average tokens/sec
-   Peak tokens/sec
-   Minimum tokens/sec

### Prompt processing

-   Prompt token count
-   Prompt processing time
-   Prompt tokens/sec

### Latency

-   Time to first token (TTFT)
-   Request latency
-   Queue/load latency where measurable

### Overall

-   Total tokens
-   Total duration
-   Tokens/sec

Example:

``` text
Performance

TTFT
0.84 s

Prompt
1,240 tokens

Prompt processing
1,850 tok/s

Generation
8,420 tokens

Generation speed
47.8 tok/s

Total
9,660 tokens
```

------------------------------------------------------------------------

# 22. API Analytics

All API requests should be included in the analytics system.

Example:

``` text
API PERFORMANCE

Requests              1,482
Input tokens         824,219
Output tokens        193,441

Prompt processing    1,742 tok/s
Generation            46.8 tok/s
Median TTFT             312 ms

GPU utilisation          97%
VRAM                  12.8 GB
Average power           274 W

Tokens / watt          0.171
```

Do not store prompt or response contents by default.

Analytics should store metadata only.

------------------------------------------------------------------------

# 23. Hardware Analytics

Sample hardware telemetry during inference.

Minimum:

-   GPU utilisation
-   VRAM utilisation
-   VRAM used
-   GPU temperature
-   GPU hotspot
-   GPU clock
-   Memory clock
-   GPU power

Where available:

-   Fan RPM
-   CPU utilisation
-   CPU temperature
-   RAM utilisation

Sampling interval:

``` text
500 ms – 1 second
```

Do not sample excessively.

------------------------------------------------------------------------

# 24. Performance Database

Use SQLite.

Suggested schema:

``` text
sessions
--------
id
timestamp
model
quantization
context_size
gpu
runtime_version
duration
prompt_tokens
generated_tokens
prompt_tps
generation_tps
ttft
peak_vram
average_power
average_gpu_utilisation
```

Suggested API request table:

``` text
requests
--------
id
timestamp
model
endpoint
prompt_tokens
completion_tokens
ttft
prompt_tps
generation_tps
duration
status
```

Do not store prompt/response content.

------------------------------------------------------------------------

# 25. Benchmark Mode

Provide:

**Benchmark**

The benchmark should run a reproducible standard workload.

Example:

``` text
128 prompt tokens
1024 generated tokens
```

Run three iterations.

Report:

-   Average
-   Median
-   Minimum
-   Maximum
-   TTFT
-   Prompt tok/s
-   Generation tok/s
-   Peak VRAM
-   Average power
-   Performance/watt

Example:

``` text
RX 6900 XT

Model: Qwen 14B Q5_K_M

Prompt processing
1,842 tok/s

Generation
48.1 tok/s

VRAM
11.3 GB

Power
286 W

Performance/W
0.168 tok/s/W
```

The benchmark must use a reproducible prompt/configuration.

------------------------------------------------------------------------

# 26. Benchmark History

Allow comparison between historical runs:

``` text
Qwen 14B Q5_K_M

             Current     Previous
Generation   48.1        45.2 tok/s
Power        286 W       310 W
VRAM         11.3 GB     11.3 GB
Temperature  63°C        69°C

Performance
+6.4%

Power
-7.7%
```

This is particularly important for GPU undervolting/overclocking.

------------------------------------------------------------------------

# 27. GPU OC/UV Analytics

RadeonLLM must **not initially modify GPU clocks or voltages**.

Adrenalin remains responsible for GPU tuning.

RadeonLLM should monitor:

``` text
GPU Performance

Clock
2,250 MHz

Memory
2,150 MHz

Power
287 W

Temperature
63°C

Hotspot
74°C
```

Benchmark comparison:

``` text
Profile A
45.2 tok/s
310 W

Profile B
47.8 tok/s
287 W

Profile B
+5.8% performance
-7.4% power
```

This allows safe evaluation of aggressive 6900 XT tuning.

------------------------------------------------------------------------

# 28. Stability Testing

Provide:

**Stress Test**

Duration options:

``` text
5 minutes
30 minutes
1 hour
Custom
```

Monitor:

-   Inference errors
-   Process crashes
-   GPU driver resets
-   VRAM utilisation
-   Temperature
-   Power
-   Token throughput

Example:

``` text
STABILITY TEST

Duration       60:00
Tokens         174,320
Average speed  48.4 tok/s
Minimum speed  47.9 tok/s
GPU errors     0
Runtime errors 0

PASS
```

------------------------------------------------------------------------

# 29. Local API Security

Default:

``` text
Bind:
127.0.0.1

Authentication:
Disabled
```

When LAN mode is enabled:

``` text
Bind:
0.0.0.0

Authentication:
Required
```

API keys should be stored securely where practical using Windows
Credential Manager or DPAPI rather than plaintext configuration.

Never expose an unauthenticated server to the LAN by default.

------------------------------------------------------------------------

# 30. API Compatibility Goal

The application should be compatible with software expecting an
OpenAI-style endpoint.

Target usage:

``` text
Base URL:
http://127.0.0.1:8080/v1

API key:
local-radeonllm-key

Model:
qwen2.5-coder-14b-q5km
```

Example generic client configuration:

``` text
Provider:
OpenAI-compatible

Base URL:
http://127.0.0.1:8080/v1

Model:
qwen2.5-coder-14b-q5km
```

This should allow external Windows development tools and applications to
consume the local model without a RadeonLLM-specific SDK.

------------------------------------------------------------------------

# 31. Privacy

Default behaviour:

**100% local.**

The application must not upload:

-   Prompts
-   Responses
-   Models
-   Source code
-   Documents
-   Telemetry

Analytics remain local.

Optional anonymous crash reporting may be added later, but must be
explicitly opt-in.

------------------------------------------------------------------------

# 32. Network Behaviour

Network access is only required for:

-   Runtime downloads
-   Model downloads
-   Optional application updates

Inference must work completely offline.

Provide:

``` text
Offline Mode
```

which disables external network access while preserving localhost API
functionality.

------------------------------------------------------------------------

# 33. Logging

Maintain:

``` text
logs/app.log
logs/runtime.log
logs/inference.log
logs/api.log
```

Use rolling logs.

Do not store complete prompts or responses in logs by default.

Record:

-   Startup failures
-   Runtime failures
-   API errors
-   GPU errors
-   Model load failures
-   Server crashes
-   Configuration errors

------------------------------------------------------------------------

# 34. Error Handling

Errors should be human-readable.

Instead of:

``` text
VK_ERROR_DEVICE_LOST
```

show:

``` text
GPU inference stopped unexpectedly.

Possible causes:
• GPU driver reset
• Unstable GPU overclock
• Insufficient VRAM
• Vulkan runtime error

Try:
[Retry]
[Reduce Context]
[Reduce GPU Offload]
[Open Diagnostics]
```

Raw errors remain available in Diagnostics.

------------------------------------------------------------------------

# 35. Automatic Recovery

For recoverable failures:

``` text
llama-server crash
      ↓
Capture error
      ↓
Record analytics
      ↓
Restart server
      ↓
Health check
      ↓
API restored
```

If repeated crashes occur:

``` text
Server failed repeatedly.

Possible unstable GPU configuration.

[Safe Mode]
[Open Diagnostics]
```

Safe Mode should use conservative inference settings.

------------------------------------------------------------------------

# 36. Distribution

Primary executable:

``` text
RadeonLLM.exe
```

Recommended initial packaging:

``` text
Self-contained .NET application
```

Optional installer:

``` text
RadeonLLM-Setup.exe
```

Recommended data directory:

``` text
%LOCALAPPDATA%\RadeonLLM\
```

Structure:

``` text
RadeonLLM/
│
├── RadeonLLM.exe
│
├── runtime/
│   └── llama.cpp/
│
├── models/
│   └── *.gguf
│
├── config/
│   └── settings.json
│
├── analytics/
│   └── sessions.db
│
├── logs/
│
└── cache/
```

Avoid Administrator privileges for normal operation.

------------------------------------------------------------------------

# 37. First-Run Experience

The ideal first-run flow:

``` text
RadeonLLM

AMD Radeon RX 6900 XT detected
16 GB VRAM
Vulkan ✓

Runtime
Not installed

[Setup]
```

Then:

``` text
Downloading inference runtime...
████████████████████ 100%

Verifying...
✓

Testing GPU...
✓

Runtime ready.
```

Then:

``` text
Select a GGUF model

[Import Model]
[Download Model]
```

Then:

``` text
Model:
Qwen 14B Q5_K_M

VRAM:
11.3 / 16 GB

Configuration:
Balanced

[Start Server]
```

Then:

``` text
Server running

API:
http://127.0.0.1:8080/v1

Model:
qwen2.5-coder-14b-q5km

[Open Chat]
[Copy API Endpoint]
[Benchmark]
```

Target:

> **Less than 5 minutes from first launch to first generated token.**

------------------------------------------------------------------------

# 38. Technology Recommendation

Recommended stack:

``` text
UI
C# / .NET 8+

Backend
C# services

Inference
llama.cpp

Inference process
llama-server.exe

GPU backend
Vulkan

Database
SQLite

Telemetry
Windows performance APIs
Vulkan/llama.cpp metrics

Process management
System.Diagnostics

Configuration
JSON

Secrets
Windows Credential Manager / DPAPI

Packaging
Self-contained EXE
```

The UI can use WinUI 3 or another lightweight Windows-native
presentation layer.

------------------------------------------------------------------------

# 39. Project Structure

``` text
RadeonLLM/
│
├── src/
│   ├── RadeonLLM.App/
│   ├── RadeonLLM.Core/
│   ├── RadeonLLM.Hardware/
│   ├── RadeonLLM.Runtime/
│   ├── RadeonLLM.Models/
│   ├── RadeonLLM.Inference/
│   ├── RadeonLLM.Analytics/
│   ├── RadeonLLM.Api/
│   └── RadeonLLM.Security/
│
├── runtime/
├── tests/
└── docs/
```

Subsystems should remain independently replaceable.

------------------------------------------------------------------------

# 40. Critical Architectural Decisions

## 40.1 Do not fork llama.cpp initially

Treat llama.cpp as an external managed runtime:

``` text
RadeonLLM
     │
     └── manages
           │
           └── llama-server.exe
```

This permits llama.cpp to be upgraded independently.

## 40.2 The API is the primary inference interface

The built-in UI must consume the same API exposed to external
applications.

Do not create a second inference path.

``` text
Built-in Chat ───────┐
                     ▼
              OpenAI API
                     │
External Apps ───────┘
                     │
                     ▼
               llama-server
```

## 40.3 Analytics must observe the API

Every request made through the local API should contribute to
performance analytics.

## 40.4 GPU tuning remains external

Adrenalin controls:

-   Core clock
-   Voltage
-   Memory clock
-   Power limit
-   Fan curve

RadeonLLM measures the consequences.

------------------------------------------------------------------------

# 41. MVP Scope

Version 0.1 should contain:

### Hardware

-   GPU detection
-   VRAM detection
-   Vulkan detection
-   Basic CPU/RAM detection

### Runtime

-   llama.cpp download
-   Runtime verification
-   Runtime launching
-   Runtime health monitoring

### Models

-   GGUF import
-   Model selection
-   Model metadata
-   VRAM preflight

### Inference

-   llama-server management
-   GPU offload
-   Context setting
-   Automatic configuration

### API

-   `/v1/models`
-   `/v1/chat/completions`
-   `/v1/completions`
-   Streaming
-   Localhost binding
-   Optional API key

### UI

-   Minimal chat
-   Server status
-   Model selection
-   API endpoint display

### Analytics

-   Prompt tokens
-   Generated tokens
-   Tokens/sec
-   TTFT
-   VRAM
-   GPU utilisation
-   Temperature
-   Hotspot
-   Memory clock
-   GPU clock
-   Power

### Benchmark

-   Standard benchmark
-   Historical results
-   Performance/watt

### Stability

-   Timed stress test
-   Error detection
-   Crash detection
-   Basic automatic recovery

------------------------------------------------------------------------

# 42. Future Features

Only after the MVP is stable:

-   Multi-GPU support
-   NVIDIA support
-   Intel support
-   Automatic model recommendations
-   Model benchmark database
-   GPU tuning recommendations
-   RAG
-   Document ingestion
-   Embedding models
-   Vision models
-   Audio models
-   Model conversion
-   Quantisation
-   Remote LAN inference
-   Web UI
-   Model pools
-   Multiple simultaneously loaded models
-   Automatic model routing

These should not be part of the initial application.

------------------------------------------------------------------------

# 43. Success Criteria

RadeonLLM is successful if a new user can:

1.  Download one executable.
2.  Launch it.
3.  Have their Radeon GPU automatically detected.
4.  Install the inference runtime with one click.
5.  Import a GGUF model.
6.  Start the inference server.
7.  See an OpenAI-compatible endpoint.
8.  Connect an external OpenAI-compatible client.
9.  Generate tokens.
10. See real-time tokens/sec and GPU statistics.
11. Run a benchmark.
12. Compare performance against previous runs.
13. Run a stability test.
14. Continue using the workstation completely offline.

The application should feel like:

> **GPU-Z + minimal LLM launcher + OpenAI-compatible local server +
> lightweight performance profiler.**

It should remain substantially simpler than a full LM
Studio/Ollama-style platform.
