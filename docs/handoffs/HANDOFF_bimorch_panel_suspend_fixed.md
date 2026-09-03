# HANDOFF: panel kẹt "paused" — đã đóng ở v0.8.30 (cả hai pane)

- **Từ:** RevitMCPServer → **Đến:** bim-orchestrator / AutoAudit team
- **Ngày:** 2026-09-03 · **Mức ưu tiên:** vừa (không cần các bạn sửa gì) · **Trạng thái:** OPEN

Phản hồi cho `bim-orchestrator/docs/revit_mcp_panel_suspend_gap.md` (báo 2026-08-25).

Ship ở **v0.8.30** (`main`, tag `v0.8.30`), deploy R2025/R2026/R2027, verify live trên Revit 2027
đúng kịch bản các bạn mô tả: mở A, mở B, đóng A — **panel tự sống lại**.

Bug report này chuẩn xác tới mức không cần điều tra thêm: đúng file, đúng số dòng, đúng cơ chế, và
phần "Why the recovery buttons are dead" chỉ thẳng vào `EnsureWebViewCore()` early-return. Cảm ơn.

## 1. Đã làm đúng cả hai đề xuất của các bạn

**A — nút Retry/Reload xoá cờ trước khi rebuild.** Thêm `ForceRebuild()`:

```csharp
private void ForceRebuild()
{
    _suspended = false;
    EnsureWebView();
}
```

Dùng cho cả `retry.Click` lẫn nhánh `else` của `reload.Click`. Đúng lập luận của các bạn: người dùng
bấm nút *chính là* tuyên bố "chuyển document xong rồi".

**B — `ViewActivated → Resume()`.** Đã wire. Sau khi đóng một document mà còn document khác mở, Revit
activate view trong document còn sống nên sự kiện này bắn và panel tự khỏi — người dùng không bao giờ
tới được fallback.

`DocumentClosing → Suspend()` giữ nguyên, đúng như các bạn khuyến nghị: nó là thứ bảo vệ interop queue
của WebView2 khỏi dialog model-upgrade. Lỗi nằm ở đường về, không nằm ở suspend.

## 2. Về dòng "Did NOT reproduce 2026-08-28" ở đầu report

Các bạn ghi rất đúng khi để nó là "evidence about ONE session, not a fix" và giữ ticket mở.

Chúng tôi **không** cố tái hiện. Thay vào đó xác minh bằng chính source, vì lỗi này mang tính **cấu
trúc** chứ không phải ngẫu nhiên:

| Sự thật | Vị trí |
| --- | --- |
| `_suspended = true` chỉ ở **một** chỗ (`Suspend`) | `AutoAuditPanelView.cs:86` |
| `_suspended = false` chỉ ở **một** chỗ (`Resume`) | `:94` |
| `Resume()` chỉ tới được từ **một** sự kiện: `DocumentOpened` | `App.cs:132-133` |
| Không tồn tại wiring `ViewActivated` nào | grep = 0 |
| `EnsureWebViewCore` return sớm khi `_suspended` | `:182` |
| **Cả hai** nút cứu hộ đều chạy vào đúng return đó | `:147`, `:113` |
| `_panelView` khởi tạo **một lần** → đóng/mở pane vẫn cùng instance | `App.cs:122` |

Suy ra: bất kỳ `DocumentClosing` nào không kèm `DocumentOpened` là panel chết cả phiên, không đường
cứu. Không phụ thuộc timing.

Và smoke 2026-08-28 **không mâu thuẫn** với kết luận đó: nó để panel mở 11 phút nhưng **không đóng
document nào** — tức chưa chạm vào cò súng. Không tái hiện ở đó là điều phải xảy ra, không phải tín
hiệu tốt. Ba biến các bạn liệt kê là "chưa thử" (đóng/mở document, mất focus lâu, restart service)
thì chỉ biến **đầu tiên** là nguyên nhân; hai biến còn lại không liên quan tới cờ này.

## 3. Hai thứ report chưa nêu, cũng đã sửa

### 3.1 Pane **Spatial QC** dính đúng lỗi đó

Report chỉ nói về pane AutoAudit. Nhưng add-in đăng ký **hai** dockable pane, và pane Spatial QC
(`App.cs`, ribbon tab "Spatial QC", cổng `:8602`) wire **y hệt** cặp `DocumentClosing`/`DocumentOpened`
với cùng ngõ cụt. Nếu chỉ sửa AutoAudit thì Spatial QC vẫn chết y như cũ.

Đã sửa cho **cả hai**: `ForceRebuild()` nằm trong class dùng chung nên ăn cả hai; `ViewActivated` được
wire riêng ở mỗi registration.

### 3.2 Pane Spatial QC tự gọi mình là "AutoAudit"

Cả hai pane đều là instance của `AutoAuditPanelView`, và class đó hardcode tên trong **3 chuỗi người
dùng đọc được**:

```
"AutoAudit panel is paused while Revit switches documents."
"Open AutoAudit in browser"
"AutoAudit keeps working at {_url}"
```

Nên pane Spatial QC báo *"AutoAudit keeps working at http://127.0.0.1:8602/ui/"* — **sai cả tên tool
lẫn cổng trong một câu**, và chỉ người dùng đi cài nhầm service. Tên pane giờ là tham số constructor.

Đáng chú ý vì nó **cùng họ** với bug các bạn báo: cả hai đều là hệ quả của việc hai pane dùng chung
một class mà không tham số hoá phần khác biệt.

## 4. Cách xác nhận

Add-in đúng build: `GET http://127.0.0.1:7892/health` → `version 0.8.30`.

Kịch bản (chính là repro trong report):

1. Mở model A, bấm ribbon **AutoAudit → AutoAudit Panel**.
2. Mở model B.
3. Đóng model A.
4. Panel phải tự sống lại. Nếu kịp thấy fallback thì **"Retry embedded view"** phải rebuild được —
   trước đây nút này trơ hoàn toàn.

Lặp lại với tab **Spatial QC**. Panel báo lỗi kết nối khi service chưa chạy là **bình thường**, không
phải bug này — thứ cần xem là nó có thoát được trạng thái "paused" hay không.

## 5. Workaround trong report có thể bỏ

Mục "Workaround until this ships" của các bạn — *"Open the model you need LAST, and don't close
another document after it"* — **không còn cần** từ v0.8.30. Nếu ghi chú đó đã vào tài liệu demo AU
thì gỡ được.

## 6. Không cần các bạn làm gì

Không đổi HTTP contract, không đổi tool surface, không đổi cấu hình panel
(`revit-mcp-panel.json` / `revit-mcp-spatialqc-panel.json` giữ nguyên định dạng và vị trí, installer
vẫn không bao giờ ghi đè chúng).

## 7. Một việc còn nợ các bạn từ handoff trước

`handoff_addin_dockable_panel.md` ghi *"still owed: live smoke in a running Revit session"*. Lần này
đã có smoke sống thật cho **đường suspend/resume** (mở A → mở B → đóng A → panel tự khỏi, xác nhận
bằng mắt trên Revit 2027). Còn phần smoke đầy đủ của panel — load được UI thật từ service `:8601` —
vẫn thuộc về các bạn vì service nằm bên đó; README của repo này giờ đã trỏ người dùng sang
`KenLP/autoaudit-bim` cho bước đó (v0.8.29, xem `handoff_addin_readme_autoaudit_panel.md` đã đóng).
