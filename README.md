# DPI Bypass Research + Thiết kế Modular App Framework (AI-maintainable)

> BDTG — 15/08/2026
> Mục tiêu: (1) Tổng hợp kiến thức DPI bypass; (2) Thiết kế kiến trúc framework cho Windows app
> với module cắm vào, tối ưu cho AI sửa/nâng cấp từng module với chi phí token thấp và
> cách ly lỗi tuyệt đối giữa các module.

---

# PHẦN 1 — DPI BYPASS: RESEARCH

## 1.1 DPI là gì và nó hoạt động ở đâu

**DPI (Deep Packet Inspection)** — thiết bị/ phần mềm của ISP nằm giữa máy bạn và internet.
Nó đọc nội dung gói tin (không chỉ header) để phân loại traffic theo **chữ ký**:

| Lớp | DPI nhìn thấy gì | Dùng để làm gì |
|---|---|---|
| TCP handshake | SYN/ACK pattern | Phát hiện kết nối |
| TLS ClientHello | **SNI (tên miền dạng plaintext)**, JA3/JA4 fingerprint, TLS version | Chặn theo domain |
| HTTP | Host header, URL, User-Agent | Chặn theo domain/pattern |
| QUIC Initial | SNI, fingerprint | Chặn UDP/HTTP3 |
| DNS | Tên miền query | DNS poisoning/block |

Hai kiểu triển khai:
- **Passive DPI** (optical splitter / port mirroring): không chặn gói, nhưng **trả lời nhanh hơn server thật** (giả mạo response) → browser nhận trang chặn.
- **Active DPI** (mắc nối tiếp): chặn/thả gói theo chữ ký.

**Điểm yếu cố hữu của DPI:** nó phải parse nhanh (Gbps) nên chỉ đọc **vài chục byte đầu** của
kết nối (ClientHello, SNI) và xử lý theo state machine đơn giản. Mọi kỹ thuật bypass đều khai
thác đúng điểm này: **làm cho vài byte đầu không khớp chữ ký, hoặc làm DPI mất đồng bộ
(desync) với server thật**.

## 1.2 Các kỹ thuật bypass chính (state of the art 2026)

| # | Kỹ thuật | Cơ chế | Tool dùng |
|---|---|---|---|
| 1 | **TCP fragmentation / segmentation** | Chia ClientHello thành nhiều segment nhỏ — DPI không ghép đủ → không thấy SNI; server thì ghép được (TCP reassembly) | GoodbyeDPI, zapret |
| 2 | **Fake packet — wrong checksum** | Chèn gói giả có chữ ký bị chặn + checksum sai: **DPI xử lý, server vứt bỏ** → DPI tưởng đã chặn, kết nối thật vẫn qua | GoodbyeDPI, zapret |
| 3 | **Fake packet — wrong TTL** | Gói giả TTL nhỏ: tới được DPI nhưng **chết trước server** | GoodbyeDPI, zapret |
| 4 | **Fake packet — wrong SEQ/ACK** | Desync state tracking của DPI (DPI theo dõi sai luồng) | zapret |
| 5 | **HTTP Host header space** | Thêm space sau `Host:` — parser DPI naive không nhận ra, server vẫn hiểu | GoodbyeDPI |
| 6 | **HTTP header fragmentation** | Chia Host header qua nhiều TCP segment | GoodbyeDPI, zapret |
| 7 | **TTL tuning cho gói thật** | Điều chỉnh TTL để DPI không thấy gói đầu tiên | zapret |
| 8 | **QUIC desync (fake Initial)** | Gửi QUIC Initial giả có fingerprint bị chặn trước → DPI whitelist luồng; handshake thật chạy sau, DPI không còn để ý | **zapret (độc quyền, mạnh nhất)** |
| 9 | **Per-host strategy (autohostlist)** | Áp chiến lược khác nhau theo domain — giảm thiểu tác dụng phụ | zapret |
| 10 | Passive: **window size / MSS** | Ép TCP nhỏ segment ngay từ đầu | GoodbyeDPI |
| 11 | **DNS redirect** | Chuyển DNS sang resolver khác port lạ để tránh DNS poisoning | GoodbyeDPI |
| 12 | **Pluggable transport** | Bọc traffic thành HTTPS/QUIC giả với JA3/JA4 spoof — DPI không có lý do chặn | NetVeil (thế hệ mới) |

## 1.3 So sánh công cụ chính

| Tiêu chí | GoodbyeDPI | ByeDPI | zapret v1 | **zapret2 (hiện tại)** | NetVeil |
|---|---|---|---|---|---|
| Platform | Windows | Android (SOCKS/VPN) | Win/mac/Linux/OpenWrt | Win/mac/Linux/OpenWrt | Windows |
| Driver | WinDivert | VpnService/raw socket | WinDivert / nfqueue | WinDivert / nfqueue | WinDivert |
| QUIC/UDP desync | ⚠️ chỉ `-q` chặn QUIC (ép fallback TCP), không desync | Hạn chế | ✅ | ✅ (phát triển tiếp) | ✅ |
| Per-host rules | ✅ `--blacklist` / `--frag-by-sni` | ❌ | ✅ | ✅ | ✅ |
| TSPU (DPI tái ghép stream) | Trung bình | Trung bình | Cao | Cao | Cao |
| Trạng thái | Ổn định, 28.6k★ | Active | **⚠️ EOL (End-Of-Life)** | **Active (commit 15/08/2026), 5.2k★** | Mới, ít phổ biến |
| Dễ dùng | ✅ preset -1..-9 | ✅ app | CLI phức tạp | CLI phức tạp | — |

**Bổ sung 2 tool nữa (đã đọc repo, 15/08):**

| Tool | Bản chất | Điểm đáng chú ý |
|---|---|---|
| **SpoofDPI** (xvzc, Go, 5k★) | Local proxy HTTP/HTTPS: chẻ ClientHello thành chunk + fake packet checksum sai | Đơn giản, nhẹ; có Windows port (SpoofDPI-Platform); ⚠️ chỉ nên lấy binary từ GitHub/package manager (cảnh báo malware); có `.agents/` — dùng agent test |
| **DNSveil** (msasanmh, .NET 6, Windows) | Secure DNS client: DNSCrypt/Anonymized DNSCrypt/DoH/DoT + **GoodbyeDPI engine** (Fragment/Fake SNI) + **rule engine per-domain dạng text** | Kết hợp DNS + DPI bypass 1 cửa; rule `domain\|rules;` rất hay (fake DNS, custom SNI, per-domain proxy, block CIDR); SSL Decryption mode (self-signed CA + Change SNI) |

### ⚠️ Điểm quan trọng cho repo của bạn
- **`bol-van/zapret` (v1) đã chính thức EOL** — README ghi rõ: *"Эта версия zapret более не
  развивается... Актуальная версия — zapret 2"*. Repo `zapret2` mới (v72.x, cập nhật 15/08/2026).
- Wrapper của bạn (`Zapret-DPI-Bypass-Wrapper`, WPF/C#/.NET 10) hiện quản lý `winws.exe` của
  **zapret v1** + blockcheck qua Cygwin → **nên nâng cấp target lên zapret2** (cấu hình,
  chiến lược, blockcheck đều đổi; winws2/engine mới).
- Hướng phát triển tương lai: **QUIC everywhere** (HTTP/3 mã hóa gần như toàn bộ handshake —
  tool chỉ xử lý TCP sẽ lỗi thời) và **post-quantum TLS** (fingerprint thay đổi lớn → cửa sổ
  vàng cho kỹ thuật mới, DPI phải học lại).

### 1.3.1 Deep-dive zapret2 + SpoofDPI + DNSveil (đọc repo 15/08/2026)

**zapret2 (bol-van, 5.2k★, cực kỳ active — commit 15/08/2026):**
- Windows: binary là **`winws2`** (v1 dùng `winws.exe`) — flags filter đổi thành
  directional (`--wf-udp-out`/`--wf-tcp-out`/`--wf-udp-in`/`--wf-tcp-in`), chiến lược
  desync giờ là **Lua instances** (`--lua-init=@zapret-lib.lua --lua-desync=fake:...`),
  còn `--hostlist-auto`, `--wf-l3`, `--wf-raw` (filter tùy biến).
- **`--ssid-filter=<ssid1,ssid2>`** 🎁: v2 có sẵn **profile theo WiFi native** — mỗi instance
  winws2 gắn 1 danh sách SSID, WinDivert tự bật/tắt theo mạng hiện tại. Đúng feature
  "Domain Profiles" của wrapper BDTG → code netsh có thể vứt.
- **`blockcheck2`** vẫn là shell script chạy qua **cygwin win-bundle** trên Windows
  (không phải native exe — đính chính nhận định trước) — flow cygwin của wrapper giữ nguyên.
- **Lua engine** (`lua/zapret-lib.lua`, `zapret-obfs.lua`, `zapret-antidpi.lua`): chiến lược
  desync viết bằng Lua, cập nhật không cần rebuild.
- **Tunnel UDP→ICMP** (`udp2icmp`): bọc WireGuard/UDP qua gói ICMP ping (code 199) để né
  ISP chặn UDP — kèm `dataxor=blob` XOR payload. Rất hợp VN (ISP hay chặn UDP/QUIC).
- Nhắm OpenWrt/embedded là chính; Windows supported (winws2 built for Cygwin x86_64);
  **không hỗ trợ macOS**.

**SpoofDPI (xvzc, Go, 5k★, Apache-2.0):**
- Local proxy HTTP(S) thuần Go: bắt kết nối, chẻ TLS ClientHello thành chunk nhỏ + gửi
  fake packet checksum sai → DPI không nhìn thấy SNI trong gói đầu. Đơn giản, ít CPU.
- Không có iOS/Android; Windows dùng qua port `SpoofDPI-Platform`; cảnh báo malware cho
  binary ngoài GitHub/package manager; repo có `.agents/` (hướng dẫn agent testing).

**DNSveil (msasanmh, .NET 6, Windows-only):**
- = DNS client (DNSCrypt/Anonymized DNSCrypt/DoH/DoT/Plain) + GoodbyeDPI engine
  (Fragment / Fake SNI / SSL Decryption với self-signed CA + Change SNI).
- **Rule engine text**: `domain|rules;` — fake DNS (`youtube.com|127.0.0.1;`), custom SNI
  (`*.googlevideo.com|sni:google.com;`), per-domain proxy/DNS, block CIDR — mẫu rule rất
  tốt để học khi làm module `profiles` của framework (Phần 2.6).

**GoodbyeDPI CLI (28.6k★) — modeset mới cần biết:**
- Modern modesets `-5..-9` (mặc định **-9**): `-f 2 -e 2 --wrong-seq --wrong-chksum
  --reverse-frag --max-payload -q` — `-q` = **chặn QUIC** (ép HTTP/3 rớt về TCP để xử lý
  được), không phải desync QUIC.
- `--fake-with-sni <domain>`: fake packet giả lập ClientHello Firefox 130 (có ECH grease).
- `--auto-ttl 1-4-10` / `--min-ttl 3`: Fake Request Mode tự dò TTL theo khoảng cách.
- `--blacklist <file>` + `--frag-by-sni`: chỉ bypass đúng domain cần thiết (tương đương
  autohostlist của zapret).
- ⚠️ Xung đột đã biết: Killer "Advanced Stream Detect", QUIK trading, ESET AV (WinDivert).

## 1.4 Ghi chú bối cảnh Việt Nam

- ISP VN (Viettel, VNPT, FPT...) triển khai DPI chặn/throttle theo domain + chặn **QUIC/UDP**
  (đặc biệt UDP với service lớn như Facebook/TikTok/YouTube) và DNS poisoning.
- Chiến lược phổ biến hiệu quả ở VN: kết hợp **fake QUIC Initial** (cho UDP) + **fragmentation/
  fake TTL** (cho TCP), chạy autohostlist để chỉ can thiệp domain cần thiết.
- Cộng đồng VN dùng nhiều: zapret (desktop), ByeDPI (Android), PowerTunnel. Nhiều fork
  "zapret-VN" tồn tại nhưng nên bám bản gốc zapret2 để nhận cập nhật.

## 1.5 Lớp 2 — Proxy protocols & obfuscation (Project Atlas)

> Nguồn: project-atlas-dbb.pages.dev (docs đầy đủ: VLESS+XTLS+REALITY, VMess+WS+TLS+CDN,
> Hysteria2, Hysteria2+FakeSNI, CDN Fronting, Traffic Obfuscation, Scenarios GFW/TSPU/Fortinet)

Zapret/GoodbyeDPI (Phần 1.2) là **lớp 1: sửa gói tin tại chỗ, không cần server**. Project
Atlas là **lớp 2: chạy qua proxy protocol có obfuscation, cần VPS**. Hai lớp bổ sung nhau:

| | Lớp 1 — Packet-level (zapret) | Lớp 2 — Proxy transport (Atlas) |
|---|---|---|
| Cần server riêng | ❌ | ✅ VPS |
| Cơ chế | Desync DPI bằng packet tricks | Bọc traffic thành thứ DPI không chặn |
| Chống active probing | Kém (chỉ chống signature) | ✅ REALITY/FakeSNI trả lời như site thật |
| Giữ IP gốc | ✅ | ❌ (đi qua server) |
| Dùng khi | ISP chặn theo domain/signature | DPI mạnh, chặn IP/throttle nặng |

### Các protocol chính (từ Atlas)

| Protocol | Cơ chế | Điểm mạnh | Cạm bẫy |
|---|---|---|---|
| **VLESS + XTLS + REALITY** | VLESS không mã hóa riêng (dựa TLS ngoài); XTLS forward thẳng TLS không decrypt lại; **REALITY giả handshake của site thật** (vd `www.microsoft.com`), probe vào thấy TLS hợp lệ | Stealth nhất hiện nay; không cần domain/cert riêng; chống active probing | Cần target TLS 1.3 + phổ biến ở vùng bạn; **không đi qua CDN được** (phải direct handshake) |
| **VMess + WS + TLS + CDN** | VMess (mã hóa riêng) bọc trong WebSocket + TLS + Cloudflare | Giấu IP server sau CDN; trông như HTTPS thường | CDN thấy metadata; VMess có thể bị fingerprint; latency CDN |
| **Hysteria2** | QUIC/HTTP3, congestion control "brutal", obfuscation **salamander**, masquerade HTTP/3 | Nhanh trên mạng lossy (10%+); native UDP relay (DNS/VoIP/game) | **QUIC/UDP có thể bị ISP chặn/throttle** (đúng trường hợp VN); hành vi băng thông có thể bị nhận diện |
| **Hysteria2 + Fake SNI** | Client gửi SNI giả (zoom.us, cloudflare.com), server có cert tự ký cho domain đó — DPI thấy QUIC bình thường tới site nổi tiếng | Stealth cao, không cần cert thật (censor soi SNI chứ không soi CA) | Fronting domain bị chặn → service chết theo; QUIC đang bị soi nhiều hơn |

### Ladder theo mức độ DPI (từ Atlas — Traffic Obfuscation)
```
Môi trường lỏng        → TLS thường là đủ
DPI trung bình         → WebSocket over TLS / gRPC
DPI mạnh (GFW/TSPU)    → REALITY / Fake SNI / obfuscation custom
```

### Scenario đối chiếu (từ Atlas)
- **GFW (Trung Quốc)**: chặn RST injection + DNS poisoning + SNI + DPI. Hoạt động: CDN+VMess+WS, Hysteria2, VLESS+TLS/XTLS, NaiveProxy, Trojan. OpenVPN/WireGuard/shadowsocks thường = chết.
- **TSPU (Nga)**: DPI đặt ở lõi mạng — đổi ISP không thoát. Hoạt động tốt nhất: **VLESS+REALITY**, VMess+WS+CDN, Hysteria2, AmneziaWG, Tor bridges.
- **Fortinet (trường/công ty)**: chỉ mở 443 → VLESS+REALITY, VMess+WS 443, Hysteria2 443, TUIC, DoH. ⚠️ Nếu firewall MITM SSL thì gần như mọi thứ lộ.

### Khuyến nghị cho VN (DPI mức trung bình — yếu hơn GFW/TSPU)
1. **Nhanh, không cần server**: zapret2 (lớp 1) — fake QUIC Initial + fragmentation + autohostlist.
2. **Cần stealth/full unblock, có VPS**: **VLESS + XTLS + REALITY** (không cần domain, chống probe tốt nhất) — hoặc **Hysteria2 + Fake SNI** nếu muốn tốc độ trên UDP (⚠️ nhưng VN ISP hay throttle UDP/QUIC → test trước).
3. Client phổ biến: **sing-box / Clash Meta** (đa nền tảng, config file — rất hợp để đưa thành module của framework, xem Phần 2.6).

---

# PHẦN 2 — THIẾT KẾ MODULAR APP FRAMEWORK CHO WINDOWS

## 2.1 Yêu cầu (từ BDTG)

1. **Framework riêng** — một bộ khung dùng chung cho các app Windows của mình.
2. **App = module cắm vào framework** — mỗi tính năng là một module độc lập.
3. **AI sửa/nâng cấp module với chi phí token thấp** — AI chỉ cần đọc *module đó*, không phải
   đọc cả solution.
4. **Cách ly lỗi cứng** — module crash/hỏng không ảnh hưởng module khác và không hạ host.

## 2.2 Quyết định kiến trúc cốt lõi: **Module = tiến trình riêng (out-of-process)**

Bảng so sánh các phương án:

| Phương án | Cách ly crash cứng | Chi phí IPC | Hot-reload | AI đọc dễ | Phù hợp |
|---|---|---|---|---|---|
| **In-proc DLL (AssemblyLoadContext)** | ❌ Crash native/StackOverflow/OOM giết host | 0 | Khó (unload ràng buộc) | ✅ | Module tin cậy, cần tốc độ |
| **MEF / DI container** | ❌ Như trên | 0 | Khó | ✅ | Cũ, ít dùng cho isolate |
| **COM out-of-proc** | ✅ | Trung bình | Khó | ❌ boilerplate | Legacy |
| **Process-per-module** (chọn) | ✅✅ crash/hang/OOM chỉ chết module | Thấp (named pipe) | ✅ đơn giản | ✅ | **Yêu cầu của bạn** |
| **Microservice (gRPC, container)** | ✅✅ | Cao | ✅ | ✅ | Quá nặng cho desktop |

**Chọn: Host (shell) + ModuleHost.exe generic + mỗi module chạy trong 1 tiến trình riêng,
IPC qua Named Pipe + JSON (System.IO.Pipes + System.Text.Json — zero dependency).**

Vì sao:
- **Cách ly thật sự**: access violation, StackOverflow, OOM, `Environment.Exit` trong module
  chỉ giết module — host và các module khác không hề hấn. Đây là thứ in-proc **không bao giờ**
  cho được.
- **.NET làm việc này tự nhiên**: ModuleHost.exe là 1 binary dùng chung, nhận `--module <path>`
  khi khởi động; module chỉ là 1 DLL + manifest. Không cần viết lại gì cho từng module.
- **Named Pipe trên Windows** là IPC rẻ nhất, bảo mật bằng ACL (chỉ host kết nối được),
  không cần thư viện ngoài, không cần port.

## 2.3 Kiến trúc tổng thể

```
┌────────────────────────────────────────────────────────────┐
│  AppHost.exe (WPF shell)                                   │
│  ┌──────────────────────────────────────────────────────┐  │
│  │ Supervisor (điều phối module)                        │  │
│  │  • start/stop/restart (exponential backoff)          │  │
│  │  • heartbeat + health check (5s)                     │  │
│  │  • crash → thu crash dump + log → restart →          │  │
│  │    disable sau 3 lần fail liên tiếp                  │  │
│  │  • hot-reload: spawn version mới, drain version cũ   │  │
│  └───────┬──────────────────────┬───────────────────────┘  │
│  Named Pipe (JSON-RPC)     Named Pipe (JSON-RPC)           │
└──────────┼──────────────────────┼──────────────────────────┘
           ▼                      ▼
   ┌──────────────┐      ┌──────────────┐
   │ ModuleHost   │      │ ModuleHost   │
   │  (process)   │      │  (process)   │
   │ ┌──────────┐ │      │ ┌──────────┐ │
   │ │ zapret   │ │      │ │ tweaks   │ │
   │ │ module   │ │      │ │ module   │ │
   │ └──────────┘ │      │ └──────────┘ │
   │ log file riêng     │ log file riêng
   └──────────────┘      └──────────────┘
```

**Nguyên tắc giao tiếp:**
- Module **không bao giờ gọi thẳng module khác** — mọi thứ qua host (hub). Host = 1 chỗ duy
  nhất biết "ai gọi ai" → dễ log, dễ debug, dễ cho AI hiểu luồng.
- Dữ liệu trao đổi chỉ là **JSON thuần** (không share object, không shared memory) → không có
  lỗi "version mismatch assembly".
- Mỗi module 1 thư mục log riêng: `logs/<module>/`, xoay 1MB, giữ 5 file.
- Crash → supervisor ghi `crashes/<module>/crash-<timestamp>.json` (exit code, last 50 log
  lines, module version, dump nếu có).

## 2.4 Quy ước module — tối ưu cho AI đọc/sửa (mục tiêu token)

### Cấu trúc 1 module (chuẩn, bắt buộc)

```
modules/
└── zapret/                      ← 1 module = 1 thư mục
    ├── module.json              ← manifest (50 dòng, machine-readable)
    ├── README.md                ← ≤ 40 dòng: mô tả, config, hành vi
    ├── src/
    │   ├── Handler.cs           ← TOÀN BỘ logic (mục tiêu ≤ 400 LOC)
    │   └── Config.cs            ← model config (mapped từ module.json)
    └── tests/                   ← (tùy chọn) test nhỏ
```

### module.json (mẫu)

```json
{
  "id": "zapret",
  "version": "1.2.0",
  "entry": "ZapretModule.dll",
  "displayName": "Zapret DPI Bypass",
  "requiresElevation": true,
  "autoStart": false,
  "health": { "timeoutSec": 10, "restartBackoffSec": [2, 5, 15, 60] },
  "config": {
    "enginePath": "bin/winws2.exe",
    "strategy": "auto",
    "domains": []
  }
}
```

### Hợp đồng module (interface duy nhất, nằm trong FrameworkSDK)

```csharp
public interface IModule
{
    string Id { get; }
    Task<ModuleStatus> StartAsync(IModuleContext ctx, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}

public interface IModuleContext
{
    // 3 thứ duy nhất module được phép dùng:
    IModuleLog Log { get; }                    // log có cấu trúc
    Task<JsonElement> CallHostAsync(string op, JsonElement args, CancellationToken ct);
    IReadOnlyDictionary<string, string> Config { get; }  // từ module.json
}

// Trạng thái module:
//  Starting → Running → (heartbeat) → Stopping → Stopped
//          ↘ Faulted (supervisor quyết định: restart / disable)
```

**Quy tắc vàng cho AI-maintainability:**

1. **1 module ≤ 500 LOC** (logic). Quá → tách. Module 300 LOC ≈ 3.000–5.000 ký tự ≈ **800–1.400 token** để AI đọc TRỌN VẸN — nằm gọn trong 1 context window, không cần RAG, không cần tóm tắt.
2. **Không shared state**: mọi thứ qua config JSON hoặc qua host. AI sửa module không cần biết module khác.
3. **Contract.md / README ≤ 40 dòng** — AI đọc README + module.json + Handler.cs là đủ 100% ngữ cảnh sửa chữa.
4. **Tên file cố định**: `Handler.cs`, `Config.cs` — AI biết chính xác đọc gì trước.
5. **Framework SDK ổn định** (`FrameworkSDK.dll`): module chỉ tham chiếu SDK; SDK version-major tăng mới phá vỡ module → nâng cấp framework không đụng module.
6. **Mọi thứ log có cấu trúc** (JSON lines) — AI đọc log = đọc dữ liệu, không phải grep chuỗi.

### Quy trình "AI sửa module" (token tối thiểu)

```
1. Module crash → supervisor tạo crash bundle:
   crashes/zapret/crash-...json  (exit code, 50 dòng log cuối, version, config)
2. AI được đưa ĐÚNG 3 thứ:
   - crash bundle (nhỏ)
   - modules/zapret/ (toàn bộ: ~1.500 token)
   - FrameworkSDK contract (1 lần, cache được)
3. AI sửa → `dotnet build modules/zapret` (chỉ build module, vài giây)
4. Supervisor hot-reload: spawn version mới, kill version cũ
   → các module khác KHÔNG bị restart
```

**Token budget điển hình:** sửa 1 bug module = đọc crash bundle (~300 tok) + module (~1.200 tok)
+ gọi API contract (~200 tok, cached) ≈ **1.700 token/lượt sửa** — thay vì đọc cả solution
(50k–500k token).

## 2.5 Nền tảng công nghệ (đề xuất)

| Thành phần | Chọn | Lý do |
|---|---|---|
| Runtime | **.NET 10** (bạn đã dùng) | AOT/trim sạch, single-file publish, WPF |
| Shell | **WPF** | Bạn quen, Windows-native |
| IPC | **Named Pipe + System.Text.Json (JSON-RPC 2.0)** | Zero dependency, ACL, AI-readable |
| Supervision | Tự viết ~200 LOC trong host | Không cần thư viện; logic minh bạch |
| Elevation | Module khai báo `requiresElevation` → host spawn ModuleHost với `runas` | Module admin (zapret, tweaks registry) tách khỏi host thường |
| Crash dump | `dotnet-dump` / Windows Error Reporting (WER) LocalDumps registry | Free, có sẵn |
| Build | 1 solution: `FrameworkSDK`, `AppHost`, `ModuleHost`, `modules/*` | `dotnet build modules/<x>` build được từng module |

## 2.6 Lộ trình áp dụng — biến Zapret-DPI-Bypass-Wrapper thành module #1

Đây là app hoàn hảo để "ăn cơm trước kẻng": nó đã có GUI + runner + profile manager.

```
Giai đoạn 0 (✅ XONG 16/08):  Scaffold framework — src/
  - FrameworkSDK (IModule/IModuleOps/ModuleManifest/JsonRpcChannel), HostLib
    (ModuleSupervisor: heartbeat, restart backoff, disable, crash bundle),
    ModuleHost.exe (generic), SmokeTest (12/12 PASS), AppHost (WPF GUI)
  - Demo: modules/HelloModule + CrashyModule (boom/exit/hang)
  - Chạy: `dotnet build` → SmokeTest.exe (kiểm tra cách ly) / AppHost.exe (GUI)

Giai đoạn 1 (✅ XONG 16/08):  Module mẫu "hello" + "crashy"
  - Chứng minh: crash 1 module (exception/Environment.Exit) → host + module kia sống,
    supervisor restart backoff 2s, disable sau 3 fail, crash bundle ghi đủ log

Giai đoạn 2 (✅ XONG 16/08): Port Zapret-DPI-Bypass-Wrapper — 3 module
  - `zapret-engine` (winws2): start/stop/status/buildArgs — redirect stdout/stderr,
    kill tree; buildArgs mapping v1→v2 (--wf-udp-out, Lua --lua-desync)
  - `blockcheck` (blockcheck2.sh qua cygwin): run/poll/cancel — watcher sentinel,
    parse SUMMARY → strategies JSON, lỗi sạch khi thiếu bundle
  - `profiles` (DomainProfile theo WiFi+domain): save/list/delete/network,
    %LOCALAPPDATA% + atomic write
  - SmokeTest mở rộng: **23/23 PASS**; bundle zapret2 đã tải về
    `bundle\zapret-win-bundle` (winws2.exe + lua + blockcheck2.sh), module.json
    đã trỏ config; AppHost scan 5 module + start all đã verify qua computer_use
  - ⏳ Còn lại: test blockcheck THẬT trên mạng VN (cần UAC winws2)

Giai đoạn 3 (sau):      Port MyOptimizationTool, 1000-IN-ONE thành module
  - Mỗi tweak = 1 module con (hoặc 1 module "tweaks" với từng action nhỏ)
  - Module `proxy-client`: bọc sing-box/Clash Meta — quản lý config VLESS-REALITY /
    Hysteria2 theo profile, chọn node, TUN mode — kết hợp với `zapret-engine` thành
    "1 nút bấm chống DPI 2 lớp" (packet-level + proxy transport)
```

### Lợi ích ngay lập tức với chính bạn
- Zapret engine (native, hay crash do driver) chạy tiến trình riêng → GUI không bao giờ bị kéo theo.
- Blockcheck chạy lâu (phút) → không block GUI (host gọi async qua pipe, có progress event).
- Muốn AI thêm tính năng "tự chọn chiến lược theo thời gian trong ngày" → AI chỉ đọc module
  `profiles` (~300 LOC), không đụng phần còn lại.

## 2.7 Cạm bẫy cần né (pitfalls)

| Cạm bẫy | Giải pháp |
|---|---|
| Module giữ pipe mở khi crash → host chờ timeout | Supervisor timeout mặc định 10s, kill tree (`taskkill /T`) |
| Module spawn con (winws2) → crash module nhưng con chết treo | ModuleHost track children, kill cả tree khi stop |
| JSON-RPC thiếu schema → AI sửa sai kiểu | `ops/` trong module.json khai báo sẵn request/response mẫu |
| Hai module cùng cần admin | Elevation theo module; host hiển thị trạng thái UAC |
| Log tiếng Việt/UTF-8 vỡ | Ép UTF-8 mọi nơi (Console.OutputEncoding, file header) |
| Named pipe security lỏng → app khác gọi được | PipeSecurity: chỉ allow current user SID |

---

# PHẦN 3 — TÓM TẮT QUYẾT ĐỊNH

1. **DPI**: nâng cấp wrapper sang **zapret2** (v1 EOL). Chiến lược VN: fake QUIC Initial +
   fragmentation + autohostlist. Theo dõi post-quantum TLS (cửa sổ fingerprint mới).
2. **Framework**: `.NET 10 + WPF host + ModuleHost.exe + Named Pipe JSON-RPC`,
   **process-per-module** (cách ly cứng), supervisor tự viết, quy ước module ≤500 LOC
   + manifest + README ≤40 dòng → AI sửa module ~1.700 token/lượt.
3. **App đầu tiên**: port `Zapret-DPI-Bypass-Wrapper` thành module `zapret-engine` +
   `blockcheck` + `profiles` trên framework.
