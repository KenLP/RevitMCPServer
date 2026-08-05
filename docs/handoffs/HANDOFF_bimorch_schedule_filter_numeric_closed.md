# HANDOFF: `configure_schedule` numeric filter — A1 + A2 đã đóng ở v0.8.23

- **Từ:** RevitMCPServer → **Đến:** bim-orchestrator
- **Ngày:** 2026-08-05 · **Mức ưu tiên:** vừa · **Trạng thái:** OPEN

Đây là **phản hồi** cho `bim-orchestrator/docs/handoff_addin_schedule_filter_numeric_value.md`.
Không yêu cầu các bạn sửa gì để hưởng fix — phần dưới chủ yếu là bằng chứng nghiệm thu và vài
chỗ dọn dẹp tuỳ chọn.

## Bối cảnh (tại sao cần)

Handoff của các bạn nêu hai defect: A1 (bridge type `value` là `z.string()`, số bị MCP input
validation chặn → `bad_envelope`) và A2 (C# đọc `GetValue<string>()` ngoài `try`, và chỉ gọi ctor
`ScheduleFilter(…, string)` nên Revit từ chối trên field Double rồi hạ thành warning im lặng).

Cả hai đã đóng ở **v0.8.23** (commit `2ee2c1b` trên `main`). Đã deploy cho **R2026 và R2027**
trên máy này; **R2025 vẫn ở 0.8.20** (cách 3 version, không bump trong đợt này).

## Đã verify khoảng trống

Trước khi viết, đã đọc code thật của các bạn để không handoff nhầm thứ đã có:

- `bim-orchestrator/src/bim_orchestrator/verify_recipes.py:163-178` — `_filter_value_text()` trả
  `str(value)` full precision. **Tương thích với fix**: C# parse bằng
  `NumberStyles.Float | CultureInfo.InvariantCulture`, nên `str()` của Python (luôn dùng dấu `.`,
  kể cả dạng mũ `1e-05`) parse đúng trên mọi locale máy chủ.
- `bim-orchestrator/tests/_mocks.py:1575-1589` — mock **raise** `RevitEnvelopeError(bad_envelope)`
  cho mọi `value` không phải `str`. Contract này giờ đã chặt hơn addin thật (xem §Dọn dẹp).

## Nghiệm thu (đo thật, không suy luận)

R27 Snowdon Towers Architectural, 149 cửa, addin v0.8.23, gọi HTTP direct port 7892:

| Dạng gửi | v0.8.22 | v0.8.23 |
|---|---|---|
| `"value": 2.9527559055118114` (number) | command chết, response rỗng | `ok:true`, `filtersAdded` có filter, **13 hàng dữ liệu** |
| `"value": "2.9527559055118114"` (string) | `ok:true` + warning, **100 hàng** | `ok:true`, không warning, **13 hàng dữ liệu** |
| Hai dạng có giống nhau không | — | **byte-identical** (so từng hàng) |
| Control (không filter) | 100 | 100 (read cap trên 149 cửa) |
| `Mark equals "S10"` (TEXT, regression) | ok | ok |
| `"value": "not-a-number"` | warning | warning y nguyên — không nuốt lỗi |

**Lưu ý cách đếm:** `get_schedule_data` trả **15** rows cho schedule đã lọc = 1 dòng header +
1 dòng trống + **13 hàng dữ liệu**. Con số 13 các bạn dự đoán là chính xác; nếu manifest của các
bạn so `len(rows)` thô thì sẽ thấy 15, không phải 13.

Nội dung 13 hàng: 9 opening Width 0, `101A` 660mm, ba cửa 864mm. Không hàng nào ≥ 900mm lọt vào
(size kế tiếp trong model là 914.4mm).

## Yêu cầu chính xác

**Không có yêu cầu bắt buộc.** Workaround string của các bạn giờ là đường đi đúng, không còn là
workaround. Ba việc tuỳ chọn:

### 1. (Tuỳ chọn) Nới `tests/_mocks.py` — hiện mock chặt hơn addin thật

`_mocks.py:1577` từ chối mọi `value` không phải `str`. Sau v0.8.23 điều đó không còn phản ánh
thực tế: bridge nhận `z.union([z.string(), z.number()])`. Nếu ai đó gửi number, mock sẽ đánh
trượt chính đường đi đã đúng.

```python
# tests/_mocks.py — thay vòng lặp chặn hiện tại
for f in filters:
    value = f.get("value")
    # v0.8.23: bridge nhận string HOẶC number. bool là subclass của int trong
    # Python nên phải loại riêng — ScheduleFilter không có dạng boolean.
    if value is not None and (
        isinstance(value, bool) or not isinstance(value, (str, int, float))
    ):
        raise RevitEnvelopeError(...)
```

Giữ nguyên `RevitEnvelopeError` cho các type khác (list/dict/bool) là hợp lý — bridge vẫn chặn
chúng thật.

### 2. (Tuỳ chọn) Bỏ workaround, gửi thẳng number

`_filter_value_text()` có thể trả `float` thay vì `str`. **Điều kiện: restart Node bridge** để
nạp schema mới — process đang chạy vẫn giữ `z.string()` cũ. Nếu không muốn động vào, giữ string
cũng cho kết quả **giống hệt** (đã đo byte-identical), nên đây thuần tuý là dọn nợ kỹ thuật.

### 3. Docstring/comment đã lỗi thời

- `verify_recipes.py:164-177` — "the STRING the addin bridge demands" không còn đúng.
- `audit_report.py:966` — "bridge's tool schema demands one" không còn đúng.

## Repro / dữ liệu mẫu

```
POST http://127.0.0.1:7892/mcp   Authorization: Bearer <token>
{"command":"create_schedule","params":{"category":"OST_Doors",
  "name":"probe","fields":["Mark","Family and Type","Width"]}}

{"command":"configure_schedule","params":{"scheduleId":<id>,
  "sortFields":[{"field":"Width","ascending":true}],
  "filters":[{"field":"Width","operator":"less","value":2.9527559055118114}]}}

{"command":"get_schedule_data","params":{"scheduleId":<id>}}
```

Đổi `value` sang `"2.9527559055118114"` (string) → kết quả phải giống hệt.

## Định nghĩa hoàn thành (DoD)

Các bạn tự kiểm được, không cần chúng tôi:

- [ ] Chạy lại `--create-verification-views` trên R27 Snowdon với addin ≥ v0.8.23.
- [ ] `AutoAudit - demo.doors.width_min` trả **13 hàng dữ liệu** (15 rows thô), không phải 100.
- [ ] `configure_schedule` cho rule đó trả `warnings` rỗng và `filtersAdded` có filter `Width`.
- [ ] Manifest chuyển `width_min` từ "created, warning" sang re-check thật → **2/5** schedule là
      filtered re-check thật (thay vì 1/5).
- [ ] Kiểm `/health` trả `version: 0.8.23` trước khi kết luận — v0.8.22 vẫn có bug.

## Ngoài phạm vi

- **A3 chưa làm.** Lỗi zod validation vẫn về client dưới dạng plain text không có error code.
  Đây là vấn đề toàn bộ tool surface, không riêng `configure_schedule`, nên tách ra. Việc quote
  raw text client-side của các bạn vẫn là cách chẩn đoán duy nhất hiện có.
- **Không unit-convert phía addin.** Giá trị đi thẳng vào `ScheduleFilter` ở internal units —
  các bạn vẫn phải convert 900mm → 2.9527559055118114 ft như đang làm.
- **Regex / canonical-format / uniqueness** vẫn không biểu diễn được bằng schedule filter của
  Revit. Ba recipe degraded của các bạn vẫn degraded — đó là giới hạn của Revit, không phải bug.
- **R2025** chưa có fix (đang ở 0.8.20). Nếu có flow chạy trên R2025 thì báo, sẽ build riêng.
