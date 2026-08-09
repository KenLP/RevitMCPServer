# HANDOFF: `create_detail_line` color/weight + `spatial_create_path_of_travel` — đóng ở v0.8.25

- **Từ:** RevitMCPServer → **Đến:** AutomatedSpatialQC (spatial-qc)
- **Ngày:** 2026-08-09 · **Mức ưu tiên:** vừa · **Trạng thái:** OPEN

Phản hồi cho HAI handoff, cùng một build:
- `revit_addin/HANDOFF_create_detail_line_color.md` (trên `main` của các bạn)
- `revit_addin/HANDOFF_create_path_of_travel.md` (branch `feat/pot-parity`)

Cả hai ship ở **v0.8.25** (`main`), build + deploy R2025/R2026/R2027, verify live trên R27 Snowdon.

## 1. `create_detail_line` — bug color/weight đã đóng, kèm 1 nâng cấp nhỏ so với sketch

Đúng chẩn đoán của các bạn: command chưa từng đọc `color`/`weight`, và `try/except: pass` ở
`revit_adapter.py:502` đã giấu chuyện đó từ đầu. Giờ:

- `color` {r,g,b} + `weight` (pen 1–16) áp qua `View.SetElementOverrides` **trong cùng transaction
  tạo curve** — một call ra một đường hoàn chỉnh, đúng sketch §2.
- **Khác sketch một chỗ, có chủ đích:** sketch lồng `weight` bên trong nhánh `color` ("`weight`
  without `color` can be ignored — maintainer's call"). Chúng tôi cho hai tham số **độc lập**:
  `weight` một mình vẫn có tác dụng. Lý do: im lặng bỏ qua tham số chính là bug đang sửa.
- Validate chặt: `weight` ngoài 1–16 → `bad_request` nêu đúng tên tham số (Revit tự ném thì message
  không nhắc chữ "weight"); channel màu ngoài 0–255 → `bad_request` (P.ColorByte sẵn có).
- **Bonus fix §2 đã gồm:** response giờ có `id` (kèm `detailLineId` giữ nguyên) — `ln.get("id")`
  của `mark_min_in_revit` hết trả `None`.
- Bridge MCP (`revit_create_detail_line`) cũng nhận `color`/`weight` — dùng chung `rgbSchema`.

**Nghiệm thu bằng pixel, không bằng mắt:** vẽ 1 đường magenta `(255,0,255)` weight 16 dài 80 m vào
"L4 Life Safety Plan" rồi export PNG qua `get_view_image` → đếm được **3108 pixel đúng
(255,0,255)**; ảnh export TRƯỚC đó có **0** pixel magenta. Baseline không màu, chỉ-`weight`,
chỉ-`color`, biên 16, và 3 case invalid đều đúng như bảng §6 của các bạn.

**Việc phía các bạn (tuỳ chọn, như §7 các bạn tự ghi):** sửa `revit_adapter.py:508` đọc `id`;
đơn giản hoá `nav/writeback.py` bỏ batching `override_element_graphics` khi đã có màu trực tiếp.

## 2. `spatial_create_path_of_travel` — ship, nhưng thực địa khác sketch ở CẢ BA điểm error-handling

Contract giữ nguyên §3 của các bạn, thêm field `warning`:

```jsonc
{ "id": 3328391, "viewId": 2156832, "lengthMeters": 15.026, "timeSeconds": 11.204,
  "warning": "ResultAffectedByCrop: the route was computed only inside the view's crop region..." }
```

**Nghiệm thu:** cặp điểm lấy từ chính PoT tay đặt (2157185, "Live/Work Unit 410" → "Stair S1")
tái tạo ra số của Revit lệch **0.14%** (15.026 m / 11.204 s vs 15.047 m / 11.220 s). Case điểm
trùng, điểm ngoài crop, view 3D đều trả lỗi sạch tức thì. Element test đã xoá, model không còn rác.

Ba điều đo được mà sketch không lường — ghi lại để các bạn khỏi vấp khi viết consumer:

1. **Lỗi đến bằng CẢ HAI đường.** `Create` có overload out-`PathOfTravelCalculationStatus`, nhưng
   điểm trùng nhau và điểm ngoài crop **ném `Autodesk.Revit.Exceptions.InvalidOperationException`**
   chứ không đi qua status (`StartAndEndPointsTooClose`/`PointOutsideActiveCrop` không bao giờ
   xuất hiện trong thực nghiệm). Add-in map cả hai về một mã `no_route`, message của Revit giữ
   nguyên văn — consumer chỉ cần bắt `no_route`.
2. **`ResultAffectedByCrop` là THÀNH CÔNG kèm cảnh báo, không phải lỗi.** Mọi Life Safety view của
   Snowdon đều bật crop, nên hầu như mọi route hợp lệ đều mang status này — kể cả cặp điểm của PoT
   tay đặt sẵn. Add-in chấp nhận nó và trả `warning`; consumer nên hiển thị warning cạnh số so
   sánh (một route bị crop giới hạn không nên đọc như phép đo "sạch"), nhưng đừng coi là fail.
3. **Dialog cảnh báo crop nổ lúc COMMIT transaction, không phải trong `Create`** — nó là failures
   processing của Revit. Với caller headless, một modal dialog trên UI thread = **treo toàn bộ
   add-in** tới khi có người bấm OK (đo thật: 400+ giây, mọi request sau xếp hàng). Đã xử ở tầng
   dispatcher: command opt-in `SuppressWarningsOnCommit` → `IFailuresPreprocessor` xoá WARNING lúc
   commit (error vẫn fail bình thường), warning chuyển thành field `warning` ở trên. 95 command
   còn lại giữ nguyên hành vi.

**Consumer cần biết:** lần `Create` đầu tiên trong một session Revit có thể mất **90+ giây**
(route-analysis warm-up; các lần sau ~10 s). Đặt HTTP timeout **≥ 3 phút** cho command này —
`RevitClient` mặc định timeout ngắn sẽ tự huỷ trước khi Revit tính xong (request vẫn chạy tiếp
phía server và element vẫn được tạo — dễ thành rác không ai biết).

## 3. Cách gọi

```bash
POST http://127.0.0.1:7892/mcp   # R2027; token: %APPDATA%\Autodesk\Revit\Addins\2027\revit-mcp-token.txt
{ "command": "spatial_create_path_of_travel",
  "params": { "viewId": 2156832, "units": "meters",
              "from": {"x": 18.211, "y": 12.073, "z": 9.804},
              "to":   {"x": 13.378, "y": 0.286,  "z": 9.804} } }
```

(`create_detail_line` như cũ, thêm `"color": {"r":220,"g":0,"b":0}, "weight": 7` tuỳ chọn.)

## 4. Cần các bạn làm gì

Không gì bắt buộc — chỉ cần add-in ≥ v0.8.25. Tuỳ chọn: hai việc consumer ở §1, và khi viết
`--benchmark` mode (§7 handoff create của các bạn) nhớ điểm timeout + semantics `warning` ở §2.
