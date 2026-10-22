# WebNowTable (WNTable)

Database client gọn nhẹ cho Windows, theo phong cách TablePlus / TablePro. Viết bằng C# .NET 10 + WPF.

## Hỗ trợ

| Database | Trạng thái |
|---|---|
| MySQL / MariaDB | Đã test với MySQL 8 (Laragon) |
| SQLite | Đã test |
| PostgreSQL | Có driver, chưa test với server thật |
| SQL Server | Có driver, chưa test (không cài SQL Server) |

## Tính năng

- Lưu danh sách kết nối, mật khẩu mã hoá bằng Windows DPAPI, nhãn môi trường (production / staging / local…).
- Sidebar danh sách bảng/view, tìm nhanh kiểu fuzzy (`Ctrl+P`), đổi database.
- Xem dữ liệu theo trang, sắp xếp phía server khi bấm tiêu đề cột, lọc `WHERE` (`Ctrl+F`).
- Sửa trực tiếp trên lưới: sửa ô, thêm/xoá dòng, đặt NULL, xem trước SQL, lưu tất cả trong một transaction (`Ctrl+S`). Cần bảng có khoá chính.
- Tab cấu trúc: cột, index, DDL.
- Tab SQL: tô màu cú pháp, gợi ý bảng/cột (`Ctrl+Space`), chạy câu lệnh tại con trỏ (`Ctrl+Enter`) hoặc cả file (`Ctrl+Shift+Enter` / `F5`), nhiều tab kết quả, dừng truy vấn (`Esc`). Hỗ trợ `DELIMITER` (MySQL), `$$` (PostgreSQL), `GO` (SQL Server).
- Giao diện sáng/tối theo Windows.

## Phím tắt

| Phím | Việc |
|---|---|
| `Ctrl+T` / `Ctrl+W` | Mở / đóng tab |
| `Ctrl+Tab` | Chuyển tab |
| `Ctrl+P` | Tìm bảng |
| `Ctrl+F` | Bộ lọc WHERE |
| `F5` / `Ctrl+R` | Tải lại |
| `Ctrl+I` / `Del` | Thêm / xoá dòng |
| `Shift+Enter` | Xem / sửa giá trị đầy đủ của ô |
| `Ctrl+S` | Lưu thay đổi |

Nhấp vào bảng sẽ mở thay cho tab hiện tại (tên tab in nghiêng). Nhấp đúp, Ctrl+nhấp hoặc nhấp chuột giữa để mở trong tab mới.

## Build

```bash
dotnet test tests/WNTable.Core.Tests
dotnet publish src/WNTable -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o dist
```

Kết quả là `dist/WNTable.exe`, cần .NET 10 Desktop Runtime.

`WNTable.exe --connect "Tên kết nối"` mở thẳng một kết nối (dùng để tạo shortcut).

## Dữ liệu

- Kết nối: `%APPDATA%\WNTable\connections.json`
- SQL soạn dở theo từng kết nối: `%APPDATA%\WNTable\queries\`
- Biến môi trường `WNTABLE_DATA_DIR` đổi thư mục dữ liệu, `WNTABLE_THEME=dark|light` ép giao diện.

## Cấu trúc

- `src/WNTable.Core`: driver từng database, tách câu SQL, sinh lệnh INSERT/UPDATE/DELETE từ thay đổi trên lưới, lưu kết nối.
- `src/WNTable`: giao diện WPF (`Theme/` chứa bảng màu và style control).
- `tests/WNTable.Core.Tests`: test tách SQL và test thật trên SQLite + MySQL (MySQL dùng database riêng `wntable_test`).
