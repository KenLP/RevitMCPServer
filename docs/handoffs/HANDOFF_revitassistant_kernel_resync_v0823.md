# HANDOFF: kernel C# đổi ở v0.8.23 — cân nhắc re-sync `extern/RevitMCPCore`

- **Từ:** RevitMCPServer → **Đến:** RevitAssistant
- **Ngày:** 2026-08-05 · **Mức ưu tiên:** thấp · **Trạng thái:** OPEN

Thông báo thay đổi kernel, không phải yêu cầu gấp. Submodule là của các bạn, bump theo lịch của
các bạn.

## Bối cảnh (tại sao cần)

`src/RevitMCP.Core/Commands/ConfigureScheduleCommand.cs` đổi hành vi ở v0.8.23: filter trên field
số (Double/Integer) trước đây **âm thầm không được áp** — Revit từ chối, code bắt exception rồi hạ
thành một dòng trong `warnings`, còn envelope vẫn `ok:true`. Schedule tồn tại nhưng không lọc.

Theo `DEPENDENCIES.yaml`, contract của các bạn là "đổi kernel C# → báo re-sync submodule". Đây là
cái báo đó.

## Đã verify khoảng trống

Chỉ đọc **metadata** submodule của các bạn, không đọc source bản copy (quy ước: bản canonical duy
nhất là `C:\Users\lep\My Drive\02 RD Projects\00 AI\RevitMCPServer`):

```
$ git -C D:/AIProjects/RevitAssistant submodule status
 bd472de3c28bfd890994011c1d7b50a6da03fa5b extern/RevitMCPCore (v0.8.19)
```

Các bạn đang pin **v0.8.19**. Từ đó tới `main` hiện tại là **10 commit**, kernel đổi **7 file,
+460/−7 dòng**:

```
CommandRegistry.cs                  |   3 +
ConfigureScheduleCommand.cs         | 111 ++++++--     <- thay đổi lần này
FindElementByUniqueIdCommand.cs     | 140 +++++++++    (v0.8.22, file mới)
GetElementInfoCommand.cs            |   4 +            (uniqueId, v0.8.21)
GetLinkedElementsCommand.cs         |   4 +            (uniqueId, v0.8.22)
GetStairsCommand.cs                 |  75 +++++++      (v0.8.20, file mới)
GetWallsCommand.cs                  | 130 +++++++++    (v0.8.20, file mới)
```

Nên: **re-sync kéo theo nhiều hơn fix này**. Đọc CHANGELOG mục 0.8.20 → 0.8.23 trước khi bump.

## Yêu cầu chính xác

Không có yêu cầu bắt buộc. Khi nào các bạn muốn bump:

```bash
git -C extern/RevitMCPCore fetch origin
git -C extern/RevitMCPCore checkout <ref>
git add extern/RevitMCPCore && git commit -m "chore: bump RevitMCPCore to <ref>"
```

**`<ref>` dùng `v0.8.23`.** Bản sửa đầu của handoff này nói chưa có tag; đã tag rồi, nên cứ pin
theo tag như thói quen của các bạn:

```bash
git -C extern/RevitMCPCore fetch origin --tags
git -C extern/RevitMCPCore checkout v0.8.23
```

## Thay đổi hành vi cần biết

Chỉ một, và chỉ chạm `configure_schedule`:

| Trước (≤ v0.8.22) | Sau (v0.8.23) |
|---|---|
| `filters[].value` là số JSON → **cả command fail** (`GetValue<string>()` ném ngoài `try`) | Nhận, và áp đúng |
| `filters[].value` là string trên field số → `ok:true` + `warnings`, filter **không được áp** | Áp đúng |
| Filter trên field TEXT | Không đổi |
| Giá trị thật sự không hợp lệ | Không đổi — vẫn là một dòng `warnings` |

**Rủi ro duy nhất khi bump:** nếu flow nào của các bạn tạo schedule có filter số mà trước giờ vẫn
"chạy được" (thật ra là không lọc), sau khi bump nó sẽ **lọc thật** và trả ít hàng hơn. Đó là sửa
đúng, nhưng nếu có test snapshot số hàng thì sẽ đỏ.

Không đụng gì tới HTTP contract: endpoint, envelope `{ok,data}/{ok,error}`, status map
(404 cho `unknown_command`/`not_found`), `/commands` shape, batch = 1 undo step — tất cả nguyên vẹn.

## Repro / dữ liệu mẫu

Nếu muốn tự kiểm sau khi bump, trên model có category Doors và parameter `Width` (đo trên R27
Snowdon, 149 cửa, 13 cửa hẹp hơn 900mm):

```
create_schedule(category="OST_Doors", fields=["Mark","Family and Type","Width"])
configure_schedule(scheduleId=<id>,
    filters=[{"field":"Width","operator":"less","value":2.9527559055118114}])
get_schedule_data(scheduleId=<id>)
```

→ 15 rows = 1 header + 1 dòng trống + **13 hàng dữ liệu**. Trước fix: 100 (read cap, tức không lọc).
Gửi `"2.9527559055118114"` dạng string phải ra kết quả giống hệt.

## Định nghĩa hoàn thành (DoD)

- [ ] `git -C extern/RevitMCPCore rev-parse HEAD` = ref đã chọn.
- [ ] Build addin của các bạn xanh (kernel target net8.0 cho R2026, net10.0 cho R2027).
- [ ] Test suite xanh; nếu có snapshot số hàng schedule thì kiểm lại theo mục "Rủi ro" ở trên.

## Ngoài phạm vi

- **Không yêu cầu bump gấp.** Không có lỗ hổng bảo mật, không có breaking change ở HTTP contract.
- **Chúng tôi không sửa/push gì sang repo các bạn** — theo quy ước liên repo.
- **Tag `v0.8.23` đã có** (2026-08-07) — không cần hỏi lại, cứ pin theo tag.
