# DPI Bypass — Research Notes

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
