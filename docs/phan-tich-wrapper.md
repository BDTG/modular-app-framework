# Phân tích sâu: Zapret-DPI-Bypass-Wrapper (BDTG)

> 15/08/2026 — Code: `C:\Users\BDTG\Projects\research\Zapret-DPI-Bypass-Wrapper` (748 LOC C#, 1 commit)
> Mục đích: đánh giá code hiện tại + kế hoạch port sang modular framework + nâng cấp zapret v1 → **zapret2**

---

## 1. Bản đồ repo hiện tại

| File | LOC | Vai trò |
|---|---|---|
| `ZapretGUI.csproj` | 12 | net10.0-windows, WPF, AllowUnsafeBlocks (không dùng) |
| `App.xaml(.cs)` | ~10 | Khởi động |
| `MainWindow.xaml(.cs)` | ~15 | Chứa ZapretControl |
| `Models/ZapretConfig.cs` | 57 | Model config + **sinh args winws (v1)** |
| `Models/DomainProfile.cs` | 10 | Profile: domain + mạng + chiến lược |
| `Services/ProfileManager.cs` | 112 | profiles.json + SSID detection (netsh) |
| `Services/ZapretRunner.cs` | 100 | Spawn/kill winws.exe (UAC runas) |
| `Views/ZapretControl.xaml(.cs)` | 442 | **Toàn bộ logic UI**: start/stop, blockcheck, parse strategy, profile CRUD |

**Luồng chính:**
1. User nhập domain → bấm **BlockCheck** → tạo .cmd chạy `cygwin bash -l -c 'blockcheck.sh'` (env: BATCH=1, WINWS, MDIG, TPWS, DOMAINS, IPV) → log + sentinel file → timer 3s poll → parse `* SUMMARY` → danh sách strategy
2. User chọn strategy → **Áp dụng & Lưu** → regex bỏ `[TYPE] ` prefix → preview args → lưu `DomainProfile` (domain + SSID hiện tại)
3. User bấm **Start** → `ZapretRunner.Start(winws.exe, args)` với `Verb=runas` (UAC) → UI khóa
4. **Load profile** theo mạng hiện tại → đổ args vào preview → Start

---

## 2. Phân tích từng class

### 2.1 `ZapretConfig.cs` — sinh args (v1)
- ✅ Tách sinh args khỏi UI — gọn, test được.
- ❌ **Flag là của zapret v1**: `--wf-udp=` / `--wf-tcp=` (không directional), `--dpi-desync=fake` (cơ chế cũ — v2 dùng Lua instances). Xem mapping ở mục 4.
- ❌ `ExecutablePath` mặc định `winws.exe` — tên v2 là `winws2.exe`.

### 2.2 `ProfileManager.cs` — profiles + SSID
- ✅ JSON đơn giản, đúng nhu cầu.
- ❌ **SSID detection mỏng manh**: parse `netsh wlan show interfaces` theo tiền tố `"SSID"` — trên Windows tiếng Việt, tên field có thể là "SSID" nhưng BSSID loại trừ OK; rủi ro locale + không xử lý multi-adapter. Fallback cứng `"Ethernet"` — sai khi máy có nhiều adapter có tên khác (vd "Ethernet 2").
- ❌ `profiles.json` lưu ở `AppDomain.BaseDirectory` — nếu cài vào `Program Files` → Access Denied khi ghi.
- ❌ Không locking / không atomic write — mở 2 instance có thể mất data.

### 2.3 `ZapretRunner.cs` — process lifecycle
- ✅ Bọc exception tốt, có event OnProcessExited.
- ❌ **2 lần UAC**: 1 lần Start (runas), 1 lần Stop (taskkill runas khi Kill(true) fail trên process elevated) — trải nghiệm tệ, taskkill là fire-and-forget → race (UI báo stopped nhưng process còn sống giây lát).
- ❌ **Mù log**: WindowStyle.Hidden, không redirect stdout/stderr → không biết winws báo gì (đây là lý do "không biết tại sao không bypass được").
- ❌ `_process.Kill(true)` trên elevated process ném exception (Access denied) → rơi vào catch → taskkill. Đúng hướng nhưng cần await + verify process thật sự chết.

### 2.4 `ZapretControl.xaml.cs` (442 LOC) — code-behind
- ✅ Blockcheck flow thực dụng: .cmd batch + sentinel + timer — hoạt động được, dọn log giữ 5 file.
- ✅ Parse SUMMARY đúng format v1 (`": winws "` lines, loại "working without bypass"/"test aborted").
- ❌ Không MVVM — 442 LOC code-behind khó test.
- ❌ **CommandPreviewTextBox editable** — user sửa tay → args hỏng mà không ai biết; Start lấy args từ preview (có check startsWith(exePath) nhưng lỏng).
- ❌ Blockcheck chạy CMD **visible** + UAC → phải bấm 2 lần (UAC + đợi cửa sổ). Không cancel được (chỉ đóng cửa sổ).
- ❌ Không tự động áp profile theo mạng khi mở app — phải bấm Load.
- ❌ Không có autostart (chạy cùng Windows) — phải mở app + Start mỗi lần.
- ❌ Regex `^\[(HTTPS|HTTP3/QUIC|HTTP)\]\s*` — format v1; blockcheck2 có thể đổi nhãn.

### 2.5 `download_zapret_binaries.ps1`
- ❌ **Tải từ `bol-van/zapret` (v1)** — asset `zapret-win-bundle-*.zip` của v1. Phải chuyển sang `bol-van/zapret2` (bundle tên tương tự nhưng chứa winws2/blockcheck2).

---

## 3. Bug/rủi ro ưu tiên sửa

| # | Vấn đề | Mức | Hướng xử lý |
|---|---|---|---|
| 1 | Target zapret **v1 EOL** | 🔴 | Chuyển winws2 + blockcheck2 (mục 4) |
| 2 | Mù log winws (không redirect) | 🔴 | Capture stdout/stderr → file log riêng; supervise health |
| 3 | 2 UAC mỗi vòng start/stop | 🟡 | 1 lần elevate duy nhất cho cả app (app manifest requireAdministrator) — framework module `requiresElevation` xử lý sẵn |
| 4 | SSID detection locale-fragile | 🟡 | Thay bằng `--ssid-filter` của winws2 (native!) hoặc Windows.Networking.Connectivity API |
| 5 | profiles.json ở BaseDirectory | 🟡 | Chuyển `%LOCALAPPDATA%\ZapretGUI\` |
| 6 | Preview args editable → args hỏng | 🟡 | Read-only preview; strategy là data không phải text |
| 7 | Blockcheck không cancel được | 🟢 | Kill tree khi user hủy |

---

## 4. Mapping zapret v1 → zapret2 (đã verify từ docs/manual.en.md của zapret2)

| v1 (wrapper hiện tại) | v2 (winws2) | Ghi chú |
|---|---|---|
| `winws.exe` | **`winws2.exe`** | WinDivert tích hợp; tự hạ integrity xuống Low sau init |
| `--wf-l3=ipv4` | `--wf-l3=ipv4` | ✅ Giữ nguyên |
| `--wf-udp=443,50000-65535` | **`--wf-udp-out=443,50000-65535`** | v2 tách hướng `-in`/`-out`; wrapper muốn outbound |
| `--wf-tcp=80,443` | **`--wf-tcp-out=80,443`** | Như trên |
| `--dpi-desync=fake` | **Lua**: `--lua-init=@zapret-lib.lua --lua-init=@zapret-obfs.lua --lua-desync=fake:blob=fake_default_tls:badsum:strategy=1` | v2 bỏ hardcode C, chiến lược = Lua instances (thứ tự `--lua-desync` = thứ tự thực thi); win-bundle có alias sẵn với "standard Lua scripts" |
| `--hostlist-auto=hostlist.txt` | `--hostlist-auto=hostlist.txt` | ✅ Còn, thêm tuning: `--hostlist-auto-fail-threshold`, `-retrans-threshold`... |
| Blockcheck: `blockcheck.sh` + cygwin | **`blockcheck2.sh`** + cygwin (win-bundle) | ⚠️ **SỬA NHẬN ĐỊNH TRƯỚC ĐÂY**: blockcheck2 vẫn là shell script chạy qua cygwin trên Windows — flow của wrapper giữ nguyên, chỉ đổi tên script + biến môi trường (BATCH=1, DOMAINS, CURL_CMD=1, SKIP_DNSCHECK=1) |
| `--hostlist-auto` per-network manual | **`--ssid-filter=<ssid1,ssid2>`** 🎁 | **v2 có sẵn tính năng profile theo WiFi**: chạy nhiều instance winws2, mỗi instance 1 chiến lược + 1 SSID list — WinDivert bật/tắt tự động theo mạng. Đúng feature Domain Profiles của wrapper, giờ là native! |
| — | `--wf-raw=<filter>` / `--wf-raw=@file` | Filter tùy biến full WinDivert |
| — | `--wf-dup-check` | Chặn 2 instance trùng filter |

**Kết luận mapping**: wrapper cần đổi ~5 dòng sinh args + 1 script download; phần lớn code giữ nguyên. Feature "profile theo WiFi" có thể bỏ hẳn code netsh — nhường cho `--ssid-filter`.

---

## 5. Kế hoạch port sang modular framework

### Phân tách module (đúng quy ước framework: ≤500 LOC, contract JSON qua pipe)

```
AppHost.exe (WPF)                          ← UI cũ (ZapretControl 442 LOC) tách:
├── Views/                                ← toàn bộ XAML + binding (MVVM nhẹ)
├── ViewModels/DpiViewModel.cs            ← state machine Start/Stop/Status (~150 LOC)
├── Services/ModuleClient.cs              ← JSON-RPC client tới 3 module (~120 LOC)
│
├── modules/zapret-engine/                ← từ ZapretRunner + ZapretConfig (~250 LOC)
│   ├── module.json  (requiresElevation: true)
│   ├── Handler.cs   — spawn winws2, redirect stdout/stderr → log, 
│   │                 health check (process alive + last log line), stop (kill tree)
│   └── Config.cs    — ZapretConfig v2 (mapping mục 4)
│
├── modules/blockcheck/                   ← từ RunBlockcheckButton_Click (~250 LOC)
│   ├── module.json  (requiresElevation: true)
│   ├── Handler.cs   — sinh .cmd → cygwin bash blockcheck2.sh, tee log,
│   │                 cancel (kill tree), parse SUMMARY → strategies JSON
│   └── StrategyParser.cs  — tách khỏi UI, test được
│
└── modules/profiles/                     ← từ ProfileManager + DomainProfile (~200 LOC)
    ├── module.json
    ├── Handler.cs   — CRUD profiles.json tại %LOCALAPPDATA% (atomic write + lock),
    │                 chuyển đổi profile cũ → config winws2 (kèm --ssid-filter),
    │                 sinh cấu hình multi-instance
    └── Config.cs
```

### IPC ops (JSON-RPC qua named pipe — contract cho AI)

| Op | Module | Args → Result |
|---|---|---|
| `engine.start` | zapret-engine | `{config}` → `{pid, ok, error}` |
| `engine.stop` | zapret-engine | `{}` → `{ok}` |
| `engine.status` | zapret-engine | `{}` → `{running, pid, lastLogLine}` |
| `blockcheck.run` | blockcheck | `{domain, ipv4, ipv6, cancelToken}` → `{strategies: [{type, args}]}` (event `progress` line) |
| `blockcheck.cancel` | blockcheck | `{}` → `{ok}` |
| `profiles.list` | profiles | `{network}` → `[{domain, strategy, rawArgs}]` |
| `profiles.save/delete` | profiles | `{profile}` → `{ok}` |
| `profiles.buildConfig` | profiles | `{network, domain}` → `{winws2Args}` (kết hợp --ssid-filter) |

### Checklist port (theo thứ tự)

1. **zapret-engine** — port ZapretRunner: thêm redirect stdout/stderr → `logs/engine/`; stop = kill tree + verify; health = process + heartbeat
2. **blockcheck** — port code blockcheck hiện tại: đổi `blockcheck.sh` → `blockcheck2.sh`, env `WINWS=winws2`, thêm `CURL_CMD=1 SKIP_DNSCHECK=1`; parse SUMMARY (giữ regex, thêm nhãn mới nếu blockcheck2 đổi format); cancel qua kill tree
3. **profiles** — port ProfileManager: đường dẫn `%LOCALAPPDATA%`, atomic write, lock; **bỏ netsh**, thay bằng: host hỏi Windows (NetworkInformation API) hoặc để winws2 `--ssid-filter` tự lo
4. **UI host** — MVVM hóa ZapretControl; preview args read-only; status realtime từ `engine.status` (poll 2s)
5. **download script** — đổi repo `bol-van/zapret2`, giữ logic tải zip + giải nén; verify asset chứa `winws2.exe` + `blockcheck2.sh`
6. **Bonus v2**: multi-profile theo WiFi (mỗi mạng 1 instance winws2 + --ssid-filter); autostart cùng Windows (framework supervisor lo); test strategy mới từ Lua

### Token budget sau port (AI sửa module)
- `zapret-engine` ~250 LOC ≈ 700 token
- `blockcheck` ~250 LOC ≈ 700 token
- `profiles` ~200 LOC ≈ 550 token
- Sửa 1 bug = đọc 1 module + crash bundle ≈ **1.300–1.600 token** (không phải 748 LOC + XAML + docs)

---

## 6. Đánh giá tổng thể

- **Chất lượng**: code thực dụng, chạy được, xử lý lỗi khá (catch đầy đủ, dọn log, sentinel), tiếng Việt rõ ràng. Điểm yếu lớn nhất là **không quan sát được winws** (mù log) và **2 lần UAC**.
- **Cơ hội**: nâng cấp v2 gần như free (mapping 5 dòng), và v2 tặng luôn `--ssid-filter` — feature đắt giá nhất của wrapper (profile theo WiFi) giờ là native, code netsh vứt được.
- **Kế hoạch**: Giai đoạn 2 của framework = port 3 module này; ước lượng 2–3 buổi làm (không tính thời gian blockcheck test trên mạng thật).
