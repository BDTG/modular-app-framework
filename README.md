# modular-app-framework

Windows app framework với **module = tiến trình riêng** (crash-isolated, out-of-process).
Mỗi module là 1 DLL + `module.json`, chạy trong `ModuleHost.exe` riêng, giao tiếp với
host qua **named pipe JSON-RPC** (zero dependency). Module crash/hang/OOM chỉ chết
module đó — host và các module khác không bị ảnh hưởng.

## Kiến trúc

```
AppHost (WinUI 3 shell)
  └── ModuleSupervisor (HostLib)
        ├── start/stop/restart — exponential backoff [2,5,15,60]s
        ├── heartbeat 5s · disable sau maxFailuresBeforeDisable lần fail
        ├── crash bundle (exit code + 50 dòng log cuối) → logs/crashes/
        └── Rescan() — quét lại modules root (giữ instance đang chạy)
              │ named pipe JSON-RPC        │ named pipe JSON-RPC
              ▼                             ▼
        ModuleHost.exe (process)      ModuleHost.exe (process)
        └── module DLL #1             └── module DLL #2
        log file riêng                log file riêng
```

- Module **không gọi thẳng module khác** — mọi thứ qua host (hub).
- Trao đổi chỉ là **JSON thuần** — không share object, không version-mismatch.
- Module bị tắt bằng file `disabled.flag` trong thư mục module (xóa file để bật lại).

## Repo structure

```
src/
  FrameworkSDK/   IModule, IModuleOps, ModuleManifest, JsonRpcChannel (SDK ổn định)
  HostLib/        ModuleSupervisor (spawn, heartbeat, backoff, crash bundle)
  ModuleHost/     ModuleHost.exe — generic host, nhận --module <dir>
  AppHost2/       WinUI 3 shell (Module Manager, module views, settings)
  SmokeTest/      Smoke test toàn hệ thống (guard: thiếu module → SKIP)
  modules/        hello + crashy (example modules)
```

## Quickstart

```bash
dotnet build src/AppHost2/AppHost2.csproj          # shell WinUI 3
src/AppHost2/bin/Debug/net10.0-windows10.0.22621.0/win-x64/AppHost2.exe

dotnet build src/SmokeTest/SmokeTest.csproj        # test toàn hệ thống
dotnet src/SmokeTest/bin/Debug/net10.0/SmokeTest.dll --modules C:/path/to/modules
```

AppHost mở thẳng **Module Manager** — quản lý mọi module ở một chỗ:

| Thao tác | Cách |
|---|---|
| Cài module | 📦 Install từ file → chọn zip (module.json + dll + bundle) → validate → copy vào modules root → xuất hiện ngay |
| Bật/tắt | ToggleSwitch → tạo/xóa `disabled.flag` (module Disabled, start bị chặn) |
| Start/Stop | Nút trên card từng module, hoặc ▶/■ Tất cả |
| Gỡ | Dialog xác nhận → stop → xóa thư mục |
| Mở chức năng | Click card → view chuyên biệt (toggle tweaks, bảng cleanup, card activation...) |

## Viết module

```
modules/<id>/
  module.json       manifest (id, version, entry, displayName, requiresElevation, config)
  <Entry>.dll       code (mục tiêu ≤ 500 LOC — đủ để AI đọc trọn 1 context)
  bundle/           (tùy chọn) binary/data — gitignored
  disabled.flag     (tùy chọn) có file này = module bị tắt
```

module.json:

```json
{
  "id": "tweaks",
  "version": "1.0.0",
  "entry": "TweaksModule.dll",
  "displayName": "System Tweaks",
  "requiresElevation": true,
  "autoStart": false,
  "health": { "pingTimeoutSec": 5, "restartBackoffSec": [2, 5, 15, 60], "maxFailuresBeforeDisable": 3 },
  "config": {}
}
```

Contract (FrameworkSDK):

```csharp
public interface IModule
{
    Task<JsonElement> HandleOpAsync(string op, JsonElement args, CancellationToken ct);
    // ops: "start"/"stop"/"status" + ops riêng của module ("list", "apply", "run", ...)
}
```

Module tham chiếu `PackageReference FrameworkSDK` (NuGet local feed — xem dưới).

## Ecosystem — 14 module, 8 repos

| Repo | Module | Chức năng |
|---|---|---|
| [modular-app-framework](https://github.com/BDTG/modular-app-framework) | hello, crashy | Examples: echo, crash demo (boom/exit/hang) |
| [zapret-engine-module](https://github.com/BDTG/zapret-engine-module) | zapret-engine | winws2 (zapret2) — packet-level DPI bypass |
| [blockcheck-module](https://github.com/BDTG/blockcheck-module) | blockcheck | blockcheck2.sh — quét chiến lược DPI cho domain |
| [profiles-module](https://github.com/BDTG/profiles-module) | profiles | Domain profiles theo WiFi/domain |
| [proxy-client-module](https://github.com/BDTG/proxy-client-module) | proxy-client | sing-box — TUN + VLESS-REALITY/Hysteria2 |
| [tweaks-module](https://github.com/BDTG/tweaks-module) | tweaks | Registry tweaks JSON-driven + rollback (port 1000INONE) |
| [startup-manager-module](https://github.com/BDTG/startup-manager-module) | startup-manager | Run keys + Startup folders, bật/tắt |
| [appx-manager-module](https://github.com/BDTG/appx-manager-module) | appx-manager | UWP packages — list/remove/clearCache |
| [system-cleanup-module](https://github.com/BDTG/system-cleanup-module) | system-cleanup | Temp/Prefetch/Recycle Bin — scan + dọn |
| [game-boost-module](https://github.com/BDTG/game-boost-module) | game-boost | Stop services + kill explorer, restore 1 nút |
| [components-remover-module](https://github.com/BDTG/components-remover-module) | components-remover | 5 scripts: Edge/OneDrive/Defender/UpdateHealth/PCHealth |
| [android-tools-module](https://github.com/BDTG/android-tools-module) | android-tools | ADB — devices/shell/killServer |
| [windows-activation-module](https://github.com/BDTG/windows-activation-module) | windows-activation | Activation status, traces (dò KMS), cleanKms, MAS activate |

**Chạy cả hệ thống:** `C:\Users\BDTG\Projects\mf-all` chứa junction tới từng module
repo; AppHost Settings → Modules root trỏ vào đó.

**Dependency:** module repos dùng `PackageReference FrameworkSDK` qua NuGet local
feed (`C:\Users\BDTG\mf-local-feed`). Framework đổi → `src/scripts/publish_local_feed.ps1`
(pack + push) → module bump version nếu cần.

---

Tài liệu liên quan: [docs/DPI-BYPASS-RESEARCH.md](docs/DPI-BYPASS-RESEARCH.md) —
nghiên cứu DPI bypass (zapret2, blockcheck2, protocols) đã làm trước khi xây framework.
