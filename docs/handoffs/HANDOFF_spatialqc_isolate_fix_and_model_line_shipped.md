# HANDOFF: `isolate_elements_in_view` đã sửa + `spatial_create_model_line` — đóng ở v0.8.29

- **Từ:** RevitMCPServer → **Đến:** AutomatedSpatialQC (spatial-qc)
- **Ngày:** 2026-09-03 · **Mức ưu tiên:** cao (có 1 việc các bạn PHẢI sửa) · **Trạng thái:** OPEN

Phản hồi cho hai handoff, cùng một build:

- `revit_addin/HANDOFF_isolate_transaction_bug.md`
- `revit_addin/HANDOFF_create_model_line.md`

Cả hai ship ở **v0.8.29** (`main`, tag `v0.8.29`), deploy R2025/R2026/R2027, verify live trên
R27 Snowdon Architectural: **18/18 check pass**.

Chẩn đoán của các bạn ở cả hai handoff đều đúng và đủ để sửa ngay — cảm ơn bảng đo sống và việc chỉ
thẳng số dòng. Có **ba chỗ** kết luận khác với spec/phân tích của các bạn; một trong đó **cần các bạn
sửa code**.

## 1. `isolate_elements_in_view` — đã sửa, nhưng không theo phương án 1

Đúng nguyên nhân: `ExecutionKind.UiAction` khiến dispatcher không cấp transaction, còn
`View.IsolateElementsTemporary()` là model change nên cần. Nút **"Cô lập"** chạy lại được.

**Chúng tôi dùng phương án 2 của các bạn (transaction riêng), không dùng phương án 1
(`UiAction` sang `ModelWrite`).** Lý do quan trọng với chính các bạn:

`BatchPolicy` từ chối batch trộn `ModelWrite` với `UiAction`. Ba lệnh `open_view`,
`select_elements`, `zoom_to_elements` đều là `UiAction`. Nếu nâng `isolate` lên `ModelWrite` thì
chuỗi `[open_view, isolate_elements_in_view, zoom_to_elements]` — **đúng chuỗi nút "3D in Revit" của
các bạn dùng** — sẽ bắt đầu bị trả `bad_request`. Giữ `UiAction` + transaction nội bộ nên:

- `isolate` vẫn **batch được** cùng các lệnh điều hướng view khác;
- vẫn giữ ngữ nghĩa dry-run no-op mà dispatcher áp cho UI action.

### Phát hiện: `reset` cũng cần transaction — kết luận trong handoff sai

Handoff ghi `isolate_elements_in_view {"reset": true}` là OK, và suy ra
`DisableTemporaryViewMode()` không đòi transaction. **Suy luận đó không đúng.**

Sau khi sửa nhánh `ids`, chạy live thì `reset` ném **đúng cùng một
`ModificationOutsideTransactionException`**. Phép đo cũ ra "OK" chỉ vì `isolate` hỏng ở *mọi* lần gọi
có `ids`, nên `reset` chưa bao giờ chạy trên một view mà isolate đã thật sự đổi — nó luôn không có gì
để dọn. Sửa nhánh này mới làm lộ nhánh kia.

Đó là lý do bản sửa gồm **hai** commit (`f3762b3` rồi `6a58a4f`): commit đầu tin theo kết luận trong
handoff và còn viết nó vào comment code; commit sau dọn lại. Giờ **cả hai nhánh** nằm trong
transaction, và tham số được parse **trước khi** mở transaction.

Đã verify cả ba trạng thái, không chỉ một:

| Ca | Kết quả |
| --- | --- |
| `reset` khi đang có isolate | PASS |
| `reset` lần 2 — không còn gì để dọn | PASS |
| `reset` với `viewId` tường minh | PASS |

## 2. `spatial_create_model_line` — đã ship

Đúng như spec mục 3/4: `start`/`end`, `viewId` chỉ để áp `color`, response `{id, length}` với
`length` **luôn mét**. `id` là `ModelCurve` thật, `GetReference()` dùng được — verify bằng cách đọc
lại qua `get_element_info`, nên hướng `create_aligned_dimension` ở mục 7 của các bạn khả thi.

`create_detail_line` không thay thế được, đúng như các bạn phân tích: nó throw `unsupported_view`
với `ViewType.ThreeD` (`CreateDetailLineCommand.cs:41-43`).

### CẦN SỬA PHÍA CÁC BẠN: `units: "mm"` không tồn tại

Spec mục 3 ghi `"units": "meters" | "feet" | "mm"`. **`"mm"` không có trong add-in này.**
`P.Xyz` chỉ phân biệt `feet`; **mọi giá trị khác đều bị coi là mét**:

```csharp
var scale = units.Equals("feet", StringComparison.OrdinalIgnoreCase) ? 1.0 : MetersToFeet;
```

Nghĩa là nếu chúng tôi làm theo spec, `{"x": 1200, "units": "mm"}` sẽ vẽ đường **dài 1200 mét** —
sai 1000 lần, không một lời cảnh báo. Nên lệnh **chỉ nhận `meters` và `feet`**, còn lại trả:

```json
{"ok":false,"error":{"code":"invalid_parameter","message":"units must be 'meters' or 'feet', got 'mm'."}}
```

**Nếu có đường code nào của các bạn gửi `units:"mm"`, nó sẽ nhận HTTP 400.** Fail rõ ràng, không sai
âm thầm — nhưng cần các bạn tự đổi sang mét trước khi gửi. `WidthProfile.min_cross_section` đã là
world XY mét thì gửi `units:"meters"`, hoặc bỏ hẳn (mặc định là mét).

Lưu ý rộng hơn: **giới hạn này áp cho mọi lệnh dùng `P.Xyz`** (`create_wall`, `create_detail_line`,
`create_aligned_dimension`, ...), không riêng lệnh mới. Nếu các bạn đang gửi `mm` ở đâu khác thì hình
học đang sai 1000 lần ở đó mà không báo. Đáng grep một lượt.

### Khác spec: chỗ nào "bỏ qua" thì chúng tôi báo, không im lặng

Spec cho `lineStyle` và `color` (thiếu `viewId`) là "bỏ qua nếu không có". Chúng tôi vẫn bỏ qua,
nhưng ghi vào `warnings` trong response:

```json
{"id": 3328473, "length": 1.2,
 "warnings": ["lineStyle 'X' not found; left at default."]}
```

Lý do: bỏ qua âm thầm thì từ phía người gọi **trông y hệt như đã áp thành công**. Cùng nguyên tắc đã
thống nhất ở handoff `create_detail_line` (v0.8.25) khi cho `weight` độc lập `color`.

## 3. Kèm theo: v0.8.28 đổi tham số sai kiểu từ 500 sang 400 (ảnh hưởng các bạn)

Chưa gửi handoff riêng nên ghi ở đây, vì `spatial_get_room_boundary` nằm trong nhóm bị ảnh hưởng.

Trước v0.8.28, mọi accessor `P.*` đọc qua `JsonNode.GetValue<T>()`, ném
`InvalidOperationException` — không phải `RevitCommandException` — nên **lọt qua error mapping và ra
HTTP 500 không có body**. Đo được trên `get_element_info`, `get_element_geometry`, `list_elements`,
`find_elements`, và **`spatial_get_room_boundary`**.

Từ v0.8.28:

| Đầu vào | Trước | Sau |
| --- | --- | --- |
| `{"id": "826376"}` (chuỗi số) | HTTP 500, không body | **nhận** — chuyển đổi chính xác |
| `{"id": "abc"}` / `5.5` / `true` | HTTP 500, không body | **400** `invalid_parameter` + tên key + kiểu mong đợi |
| `{"id": 999999999}` | 404 `not_found` | không đổi |
| thiếu key | `bad_request` | không đổi |

v0.8.29 mở rộng sang **phần tử mảng** (`P.LongFrom`): `{"ids": ["abc"]}` giờ ra
`Parameter 'ids[0]' must be an integer, got "abc".` thay vì 500.

**Việc cần các bạn xem:** nếu `revit_adapter.py` có nhánh nào coi HTTP 500 là "lỗi hạ tầng nên
retry" thì lỗi kiểu tham số giờ là **400, không nên retry**. `is_unknown_command_error` không bị ảnh
hưởng (vẫn `unknown_command`/404). Còn 28 file command vẫn gọi `GetValue<>()` trần trên phần tử mảng
— sẽ quét nốt, nhưng chưa xong ở bản này.

## 4. Cách xác nhận phía các bạn

Đúng build:

    GET http://127.0.0.1:7892/health      -> version 0.8.29, commandCount 100

Bug isolate đã đóng:

    POST /mcp  {"command":"isolate_elements_in_view","params":{"ids":[619340]}}
    -> {"ok":true,"data":{"viewId":...,"isolated":1,"temporary":true}}

    POST /mcp  {"command":"isolate_elements_in_view","params":{"reset":true}}
    -> {"ok":true,"data":{"viewId":...,"reset":true}}

Lệnh mới:

    POST /mcp  {"command":"spatial_create_model_line","params":{
                 "start":{"x":10.2,"y":5.4,"z":0.05},
                 "end":{"x":10.2,"y":6.6,"z":0.05},"units":"meters"}}
    -> {"ok":true,"data":{"id":<id>,"length":1.2}}

Cần token Bearer như mọi lệnh khác (`revit-mcp-token.txt` trong thư mục Addins).

## 5. Tóm việc phía spatial-qc

1. **Bắt buộc nếu đang dùng:** bỏ `units:"mm"` ở mọi call dùng `P.Xyz` (grep cả repo, không chỉ lệnh
   mới).
2. Bật lại bước vẽ chord trong `panel.py::_open_3d` — probe `is_unknown_command_error` sẽ không còn
   kích hoạt cho `spatial_create_model_line`.
3. Kiểm nút "Cô lập" trên model sống, rồi điền mục "Nghiệm thu" trong handoff gốc của các bạn.
4. Xem lại nhánh xử lý HTTP 500 nếu có retry-on-5xx (mục 3 ở trên).
5. Tuỳ chọn: đọc `warnings` trong response `spatial_create_model_line` để biết `color`/`lineStyle` có
   được áp hay không.

## 6. Không cần làm gì cho

- Contract `/commands`, envelope `{ok,data}` / `{ok,error}`, batch = 1 undo step: **không đổi**.
- MCP tool surface vẫn **93** — lệnh mới là HTTP-only đúng quy ước pack `spatial_*` (tổng 100 lệnh
  C#, 10 ẩn).
