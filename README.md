# Antigravity Account Switcher

**Tiếng Việt** · [English](README.en.md)

[![Release](https://img.shields.io/github/v/release/binhake/antigravity-account-switcher)](../../releases/latest)
[![Downloads](https://img.shields.io/github/downloads/binhake/antigravity-account-switcher/total)](../../releases)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6)
[![License](https://img.shields.io/github/license/binhake/antigravity-account-switcher)](LICENSE)

Công cụ nhỏ cho Windows giúp **lưu và chuyển nhanh giữa nhiều tài khoản Google** trên **Antigravity 2.0** và **Antigravity IDE**, không cần đăng xuất / đăng nhập lại bằng tay.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/screenshot-dark.png">
  <img alt="Giao diện Antigravity Account Switcher" src="docs/screenshot-light.png">
</picture>

<sub>Ảnh tự đổi theo giao diện Sáng / Tối của GitHub bạn đang dùng. Dữ liệu trong ảnh là tài khoản giả.</sub>

> ⚠️ Đây là công cụ **không chính thức**, không liên quan hay được Google xác nhận. Dùng với tài khoản của chính bạn và tự chịu trách nhiệm.

## Tính năng

- Lưu không giới hạn tài khoản, chuyển bằng **nhấp đúp** hoặc nút **Đổi sang tài khoản kế tiếp** (xoay vòng).
- Áp dụng cho **Antigravity 2.0**, tùy chọn áp dụng đồng thời cho **Antigravity IDE**.
- Tự đóng ứng dụng trước khi đổi và tự mở lại sau khi đổi (có thể tắt).
- Tự sao lưu tài khoản đang dùng trước mỗi lần đổi để khôi phục khi có lỗi.
- Gắn tag cho từng tài khoản, sắp xếp thứ tự, hiện ảnh đại diện Google.
- Xuất / nhập Session ra file `.json` để chuyển sang máy khác.
- Giao diện kiểu Windows 11 với chế độ Sáng / Tối / theo hệ thống, hỗ trợ màn hình DPI cao.
- Thu nhỏ xuống khay hệ thống, đổi tài khoản ngay từ menu khay.
- Mỗi lỗi được ghi thành một file riêng trong thư mục `logs\` để dễ gửi báo lỗi.

## Yêu cầu

- Windows 10 hoặc Windows 11 (64-bit).
- .NET Framework 4.x (đã có sẵn trên Windows 10/11).
- Đã cài Antigravity 2.0 (và Antigravity IDE nếu dùng tính năng IDE).

## Cài đặt

1. Tải `AntigravitySwitcher.exe` ở mục [Releases](../../releases).
2. Đặt vào một thư mục riêng (ví dụ `D:\Tools\AntigravitySwitcher\`). Ứng dụng lưu dữ liệu vào thư mục `profiles\` nằm cạnh file `.exe`, nên thư mục này phải ghi được (tránh `C:\Program Files`).
3. Chạy `AntigravitySwitcher.exe`. Muốn mở nhanh thì chuột phải file → **Show more options** → **Send to** → **Desktop (create shortcut)**.

### Trình diệt virus cảnh báo?

**Gặp màn hình xanh "Windows protected your PC"?** Bấm **More info** (Thông tin thêm) → **Run anyway** (Vẫn chạy). Chỉ cần làm một lần.

Màn hình này là **SmartScreen**, không phải trình diệt virus. SmartScreen không quét mã độc trong file. Nó chỉ cảnh báo vì file **mới và chưa ký số**, chưa có đủ lượt tải để Windows "quen mặt". Cảnh báo sẽ tự thưa dần khi có thêm nhiều người tải.

**Kết quả quét VirusTotal của bản v1.0.0** ([xem chi tiết](https://www.virustotal.com/gui/file/31aca069aa699f68429b706789f3557e9dc1ee5d4cbd17235347412b29433faf)):
- **Microsoft Defender, Kaspersky, ESET, Bitdefender** và phần lớn khoảng 70 hãng: **không phát hiện gì**.
- Chỉ khoảng 5 hãng báo, và đều là **dự đoán bằng máy học** (tên cảnh báo có chữ `ML`, `Heur`, `Anomalous`…), không phải nhận diện một mã độc cụ thể.

Các bộ máy học hay báo nhầm vì:
- file `.exe` chưa được ký số, ít người dùng;
- ứng dụng làm những việc mà phần mềm độc hại cũng hay làm: đọc Windows Credential Manager, đọc/ghi token của Antigravity, đóng tiến trình Antigravity.

ESET có thể giữ file lại phân tích vài phút trong lần chạy đầu, rồi cho chạy bình thường.

Mã nguồn nằm trọn trong một file `AntigravitySwitcher.cs` để bạn tự kiểm tra, và bạn có thể [tự biên dịch](#biên-dịch-từ-mã-nguồn) thay vì dùng file `.exe` tải về. Nếu tin tưởng, hãy thêm thư mục chứa ứng dụng vào danh sách ngoại lệ (Exclusions) của trình diệt virus.

## Cách dùng

1. Đăng nhập một tài khoản trên Antigravity như bình thường.
2. Bấm **Lưu TK đang đăng nhập**.
3. Lặp lại với các tài khoản khác.
4. Chọn tài khoản trong bảng rồi bấm **Áp dụng TK đang chọn** (hoặc nhấp đúp vào dòng đó).

Tích **Antigravity IDE** ở mục **Áp dụng** nếu muốn lưu / đổi tài khoản cho cả IDE. Nếu ứng dụng cài ở thư mục khác mặc định, bấm **Định vị lại file thực thi…**.

Lưu tài khoản từ dòng lệnh (không mở cửa sổ, mã thoát `0` = thành công, `1` = lỗi):

```bat
AntigravitySwitcher.exe --save-current
```

## Dữ liệu được lưu ở đâu

| Đường dẫn (cạnh file `.exe`) | Nội dung |
|---|---|
| `profiles\accounts.json` | Danh sách tài khoản và phiên đăng nhập (**có token**). |
| `profiles\switcher_settings.json` | Cài đặt của ứng dụng. |
| `profiles\avatars\` | Ảnh đại diện đã tải. |
| `profiles\last_rollback_backup.json` | Bản sao lưu tự động trước mỗi lần đổi tài khoản (**có token**). |
| `logs\Error_<ngày>_<giờ>.txt` | Mỗi lỗi một file. Mở hoặc xóa bằng nút **Log lỗi** trong khung **Nhật ký & chẩn đoán**. |

Muốn gỡ ứng dụng thì chỉ cần xóa cả thư mục. Ngoài dữ liệu đăng nhập của chính Antigravity (Windows Credential Manager, `app_storage.json`, `state.vscdb` của IDE), ứng dụng không ghi vào Registry hay thư mục nào khác.

## Gặp lỗi?

1. Mở khung **Nhật ký & chẩn đoán** ở cuối cửa sổ để xem chuyện gì đã xảy ra.
2. Bấm **Copy thông tin lỗi**, hoặc lấy file mới nhất trong thư mục `logs\`.
3. Tạo [Issue](../../issues) và dán nội dung vào. File log có email và tên máy nhưng **không** có token. Bạn vẫn nên xem lại trước khi đăng công khai.

Nếu file `accounts.json` bị hỏng, ứng dụng không ghi đè lên mà giữ lại một bản sao `accounts.unreadable-<thời gian>.json` để bạn khôi phục.

## Bảo mật — đọc trước khi dùng

- Phiên đăng nhập (OAuth token) của mọi tài khoản được lưu **dạng văn bản thường** trong `profiles\accounts.json`. Ai có file này là đăng nhập được tài khoản của bạn.
- File xuất Session (`antigravity_sessions_*.json`) cũng chứa token. Chỉ gửi qua kênh an toàn và xóa sau khi nhập xong.
- **Không** commit / chia sẻ thư mục `profiles\` hay các file Session.
- Ứng dụng không gửi token đi đâu ngoài máy chủ của Google:
  - `oauth2.googleapis.com`: chỉ khi lưu tài khoản từ Antigravity IDE, để lấy thông tin tài khoản.
  - `lh3.googleusercontent.com`: tải ảnh đại diện công khai, không kèm token.
- OAuth client của Antigravity **không** nằm trong mã nguồn. Ứng dụng đọc nó từ file `language_server` của Antigravity đã cài trên máy, rồi lưu cục bộ trong `profiles\switcher_settings.json`.

## Biên dịch từ mã nguồn

Không cần Visual Studio, chỉ dùng trình biên dịch C# có sẵn trong .NET Framework. Chạy trong PowerShell tại thư mục gốc của repo:

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /win32icon:AntigravitySwitcher.ico /out:AntigravitySwitcher.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll AntigravitySwitcher.cs
```

Trình biên dịch này chỉ hỗ trợ **C# 5**. Không dùng `$"..."`, `?.`, `nameof`, `out var` hay thân hàm `=>`.

## Cấu trúc repo

| Đường dẫn | Nội dung |
|---|---|
| `AntigravitySwitcher.cs` | Toàn bộ mã nguồn (một file). |
| `AntigravitySwitcher.ico` | Icon nhúng vào file `.exe`. |
| `docs/` | Ảnh chụp màn hình dùng trong README. |
| `.github/ISSUE_TEMPLATE/` | Mẫu báo lỗi / đề xuất tính năng. |
| `legacy/antigravity_switcher.py` | Bản Python (Tkinter) đời đầu, không còn được cập nhật. |

## Đóng góp

Báo lỗi hoặc đề xuất tính năng ở mục [Issues](../../issues). Pull request luôn được chào đón. Hãy đọc mục [Biên dịch từ mã nguồn](#biên-dịch-từ-mã-nguồn) và giữ đúng cú pháp C# 5.

Nếu công cụ giúp ích cho bạn, hãy để lại một ⭐ cho repo, để nhiều người khác cũng tìm thấy nó.

## Giấy phép

[MIT](LICENSE) © Binhake
