# HANDOFF: `spatial_get_paths_of_travel` đã ship ở v0.8.24 — kèm đáp án 2 câu hỏi mở

- **Từ:** RevitMCPServer → **Đến:** AutomatedSpatialQC (spatial-qc)
- **Ngày:** 2026-08-09 · **Mức ưu tiên:** vừa · **Trạng thái:** OPEN

Đây là **phản hồi** cho `revit_addin/HANDOFF_get_paths_of_travel.md` (branch `feat/pot-parity`,
chưa merge). Command đã ship, **contract đúng y như các bạn đặc tả** — `fetch_paths_of_travel`
không cần sửa gì. Phần dưới là bằng chứng nghiệm thu, đáp án cho 2 chỗ handoff tự đánh dấu
"unverified", và 2 việc các bạn nên cân nhắc.

## Trạng thái

**Ship ở v0.8.24** (`main`), đăng ký dưới tên `spatial_get_paths_of_travel` đúng convention
`spatial_*` — `RevitClient.call("get_paths_of_travel")` tự resolve qua `/commands`, không cần đổi
consumer. Read-only, không transaction. Đã build và deploy **R2025 / R2026 / R2027**; verify live
trên **R2027** (`/health` → `version 0.8.24`, `commandCount 95`).

## Hai câu hỏi mở trong handoff — đã chốt bằng RevitAPI.dll thật

Handoff §2 tự cảnh báo "not verified against a live Revit session" và dự đoán ít nhất 2 chỗ sai.
Đúng cả hai. Đã dump metadata `RevitAPI.dll` (Revit 2027) bằng `MetadataLoadContext` thay vì đoán:

**1. API lấy route curve — `GetCurve()` KHÔNG tồn tại.**

| Sketch trong handoff | API thật |
|---|---|
| `pot.GetCurve()` → `Curve` | `pot.GetCurves()` → `IList<Curve>` |
| `curve.NumberOfCurveLoops` | không có (đó là API của `Solid`/`CurveLoop`, không phải `Curve`) |

Cách lấy endpoint đúng: `curves[0].GetEndPoint(0)` và `curves[^1].GetEndPoint(1)`.
Element chưa compute được route (`GetCurves()` rỗng) bị **skip**, không emit — một dòng
`lengthMeters: 0` sẽ bị `benchmark_pot` đọc như một phép đo thật rồi cho ra `delta_pct` vô nghĩa.
Trên Snowdon không có element nào rơi vào nhánh này (95/95 đều ra curve).

Ngoài ra `PathOfTravel` còn có `PathStart`/`PathEnd` (điểm người dùng **click**) và
`GetWaypoints()`. Chúng tôi **không** dùng chúng: handoff §3 nói rõ `from`/`to` là đỉnh đầu/cuối
của **route curve** ("NOT necessarily the exact points the user clicked"), và đó cũng là thứ đúng
để so với grid router.

**2. Tên parameter — `"Actual Length"` / `"Actual Time"` KHÔNG tồn tại.**

Không hề có `PATH_OF_TRAVEL_LENGTH`. Toàn bộ `BuiltInParameter` họ `PATH_OF_TRAVEL_*` chỉ gồm:
`_TIME`, `_LEVEL_NAME`, `_VIEW_NAME`, `_SPEED`, `_FROM_ROOM`, `_TO_ROOM`. Chiều dài nằm ở
parameter dùng chung của curve element:

| Trường | BuiltInParameter | Tên trên Properties palette | Đơn vị nội bộ |
|---|---|---|---|
| `lengthMeters` | `CURVE_ELEM_LENGTH` | "Length" | feet |
| `timeSeconds` | `PATH_OF_TRAVEL_TIME` | "Time" | **giây** (không cần quy đổi) |
| `levelName` | `PATH_OF_TRAVEL_LEVEL_NAME` | "Level" | string |

Fallback: length thiếu → tổng `Curve.Length`; level thiếu → `OwnerView.GenLevel?.Name`;
`timeSeconds` thiếu → **`null`**, không phải `0` (giữ đúng nguyên tắc "unmeasured ≠ 0" mà
`spatial_get_stairs` đã đặt ra cho `riserVariation`).

> **Đáp án này dùng lại nguyên xi cho `HANDOFF_create_path_of_travel.md` §2** — cùng một element
> type, cùng bộ parameter. Không cần điều tra lại khi phía WRITE được pick up.

## Nghiệm thu (đo thật, R27 Snowdon Towers Architectural, addin v0.8.24)

Model có sẵn 95 element Path of Travel trong các view Life Safety Plan.

| Kiểm tra | Kết quả |
|---|---|
| `FilteredElementCollector` vs `list_elements(OST_PathOfTravelLines)` | **95 / 95** — không element nào bị skip ngầm |
| `levelName` null | 0 |
| `timeSeconds` null | 0 |
| `lengthMeters <= 0` | 0 |
| `from == to` (suy biến) | 0 |
| `lengthMeters >= ` khoảng cách thẳng `from→to` | **95/95 đúng** (route không bao giờ ngắn hơn dây cung) |
| `from.z == to.z` | 95/95 (PoT là phẳng, đúng contract "một view, một tầng") |
| Mỗi `levelName` ứng đúng **một** cao độ z | 7/7 level |
| `lengthMeters` min / trung bình / max | 3.683 / 14.666 / 28.093 m |

**Đối chiếu từng số với Properties palette của Revit** (element `2157185`, "Live/Work Unit 410" →
"Stair S1", view "L4 Life Safety Plan"):

| Trường | Command trả | Revit `get_element_info` | Palette hiển thị |
|---|---|---|---|
| `lengthMeters` | `15.046939524608867` | `Length` = 49.366599490186566 ft | **`15047`** mm ✅ |
| `timeSeconds` | `11.219681702315128` | `Time` = 11.219681702315128 | **`11.2 s`** ✅ |
| `levelName` | `"L4"` | `Level` = "L4" | `L4` ✅ |

**Kiểm tra chéo độc lập:** `lengthMeters / timeSeconds` trên cả 95 element ra hằng số
**1.341120 m/s**, đúng bằng parameter `Speed` = 4.4 ft/s (4.4 × 0.3048 = 1.34112). Length và time
do đó nhất quán với nhau — không phải hai con số lấy từ hai nguồn lệch nhau.

Phân bố theo tầng: `L1 - Block 35` (23), `L4` (19), `L3` (16), `L5` (15), `L2` (11), `Parking` (9),
`L1 - Block 37` (2).

## Hai việc phía các bạn nên cân nhắc (không bắt buộc)

**1. `levelName` là tên tầng của Revit, không phải của IFC — `gridset.storey(level)` sẽ trượt.**

`benchmark_pot` hiện làm `if gridset.storey(level) is None: note = "level not in grid"`. Trên
Snowdon, tên tầng Revit là `L1 - Block 35`, `L1 - Block 37`, `Parking` — nếu `GridSet` dựng từ IFC
mà tên storey khác (`Level 1`, `IfcBuildingStorey` name khác), **mọi hàng sẽ ra `"level not in
grid"`** và bảng benchmark rỗng dù command chạy đúng. Đây là rủi ro tích hợp thật, không phải bug
phía add-in — chúng tôi trả tên Revit verbatim là đúng contract §3. Gợi ý: map tên theo cao độ z
(mỗi level ở đây ứng đúng một z duy nhất, xem bảng trên) thay vì so string.

**2. Có sẵn `From Room` / `To Room` nếu cần, chưa đưa vào contract.**

`PATH_OF_TRAVEL_FROM_ROOM` / `_TO_ROOM` trả thẳng tên phòng (`"Live/Work Unit 410"` → `"Stair S1"`).
Không thêm vào output vì handoff §3 không yêu cầu và `fetch_paths_of_travel` không đọc. Nhưng nó
sẽ làm bảng benchmark đọc được hơn hẳn ("PoT 2157185: Live/Work Unit 410 → Stair S1" thay vì một
id trần), và cho phép đối chiếu PoT với room graph của `bim-nav` mà không cần point-in-polygon.
Nếu muốn, mở một handoff nhỏ xin thêm 2 field — thuần additive, không phá ai.

## Cách dùng

```bash
# R2027 (port = 7891 + year - 2026). Token: %APPDATA%\Autodesk\Revit\Addins\2027\revit-mcp-token.txt
POST http://127.0.0.1:7892/mcp
{ "command": "spatial_get_paths_of_travel", "parameters": {} }
```

Output đúng như contract §3, thêm `count` ở ngoài cùng cho đồng bộ với `spatial_get_walls` /
`spatial_get_stairs` (`fetch_paths_of_travel` chỉ đọc `paths`, nên vô hại):

```jsonc
{ "count": 95,
  "paths": [ { "id": 2157185, "levelName": "L4",
               "from": {"x": 18.211, "y": 12.073, "z": 9.804},
               "to":   {"x": 13.378, "y": 0.286,  "z": 9.804},
               "lengthMeters": 15.047, "timeSeconds": 11.220 } ] }
```

## Cần các bạn làm gì

Không gì để hưởng feature — chỉ cần add-in ≥ v0.8.24. Việc tuỳ chọn: merge `feat/pot-parity`, chạy
`bim-nav benchmark-pot` (nhánh "add-in chưa hỗ trợ" giờ sẽ không kích hoạt nữa), và xử lý điểm 1
ở trên nếu bảng ra toàn `"level not in grid"`.
