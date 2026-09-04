# HANDOFF: `create_aligned_dimension` từ chối view 3D — đã đóng trên `main` (0.8.32, chưa tag)

- **Từ:** RevitMCPServer → **Đến:** AutomatedSpatialQC (spatial-qc)
- **Ngày:** 2026-09-04 · **Mức ưu tiên:** vừa (không chặn việc gì của các bạn) · **Trạng thái:** OPEN

Phản hồi cho `revit_addin/HANDOFF_dimension_in_3d_view.md`.

Đã sửa, verify sống trên Revit 2027 / R27 Snowdon Architectural.

**⚠️ Về version:** bản sửa nằm trên `main` ở **0.8.32**, commit `753eff2` — **chưa tag**. Tag mới
nhất vẫn là `v0.8.31`. Ken chủ động gom lại để bớt nhịp release, nên **đừng đi tìm release
v0.8.32**; lấy `main`, hoặc kiểm bằng `/health` (`version: 0.8.32`, `gitCommit 753eff2`).

Handoff của các bạn viết rất tốt: đối chứng `OST_Lines` là thứ làm nó thành bằng chứng thay vì phỏng
đoán, và việc dẫn lại chính nguyên tắc chúng tôi đặt ra ở `spatial_create_model_line` là lập luận
đúng chỗ. Nhưng **đề xuất kỹ thuật của các bạn chưa đủ** — chi tiết ở mục 1.

## 1. Đề xuất (a) của các bạn chặn thiếu: điều kiện không phải `!IsLocked`

Các bạn đề nghị:

```csharp
if (view is View3D && !view.IsLocked) -> invalid_parameter
```

Suy đoán nền là *"dimension chắc cũng như tag, lock view rồi thì đặt được"*. **Ken đã kiểm trực tiếp:
lock rồi vẫn KHÔNG hiện.**

Nếu chúng tôi làm đúng nguyên văn (a), thì một view 3D **đã lock** vẫn lọt qua guard và vẫn tạo ra
đúng phần tử vô hình mà handoff này muốn diệt — tức bug còn nguyên, chỉ hẹp hơn.

Nên guard phủ **mọi `View3D`**, không quan tâm lock:

```csharp
if (view is View3D)
    throw new RevitCommandException("invalid_parameter", ...);
```

Thông điệp lỗi nói thẳng lock không cứu được, để người gọi sau khỏi mất công thử lại đúng thí nghiệm
đó:

```
View 3328478 is a 3D view. Revit does not display dimensions in a 3D view even after
View > Lock 3D View, although the API will create one and report success. Place the
dimension in a plan, section, elevation, drafting or detail view instead.
```

**Chọn (a) từ chối, không chọn (b) cảnh báo** — chính vì lock đã bị loại. Nếu lock có tác dụng thì
(b) hợp lý hơn: phần tử có đường trở thành hữu ích, chỉ thiếu một bước. Nhưng khi không còn đường nào
để nó dùng được, trả về `dimensionId` chỉ là đánh lừa người gọi.

**Không thêm `lock_3d_view` (mục (c) của các bạn)** — nó sẽ mở một cánh cửa dẫn vào ngõ cụt. Các bạn
cũng đã tự quyết bỏ hướng này (mục 6), nên hai bên khớp nhau.

## 2. `valueMetres` — và một chỗ repo chúng tôi tự mâu thuẫn

Đúng như mục 5 của các bạn: `value` là **feet** bất kể `units`, vì `units` chỉ áp cho toạ độ đầu vào
(`P.Xyz`). Đã thêm `valueMetres` cạnh nó.

Đo sống, hai điểm cách nhau đúng 1,60 m:

```json
{"dimensionId": 3328497, "value": 5.2493438320209975, "valueMetres": 1.6}
```

Tỉ số `value / valueMetres = 3.28084` — **chính con số chứng minh `value` là feet**, không phải chúng
tôi tin theo lời handoff.

Điều các bạn phát hiện còn chỉ ra một chỗ **repo chúng tôi tự mâu thuẫn**:
`spatial_create_model_line` trả `length` **luôn mét**, còn lệnh này trả `value` **feet**. Hai lệnh
cùng họ, hai đơn vị. Giờ trả cả hai số nên không còn chỗ đọc nhầm.

## 3. Tối thiểu 2 reference — đã ghi tài liệu, không đổi hành vi

Giữ nguyên `references.Count >= 2` (một dimension đo *giữa* hai thứ). Đã ghi vào docstring và mô tả
MCP tool, kèm đúng ca của các bạn: đo bề rộng bằng **một** chord `ModelCurve` chỉ cho một reference,
nên phải thêm mốc thứ hai.

Lưu ý phía Node: schema đã có `.min(2)` từ trước, nên caller đi qua MCP tool bị chặn ngay ở tầng
schema; caller đi HTTP trực tiếp (như các bạn) thì gặp `bad_request` ở tầng C#.

## 4. Kèm theo: `references[].elementId` thiếu key giờ ra 400, không phải 500

Không nằm trong handoff, tìm thấy khi sửa. Code cũ là `refObj["elementId"]!.GetValue<long>()` — thiếu
key là `NullReferenceException` → **HTTP 500**. Giờ đi qua `P.LongFrom`:

```json
{"ok":false,"error":{"code":"invalid_parameter","message":"'references[].elementId' is null."}}
```

Cùng lớp việc với v0.8.28/0.8.29 đã báo các bạn ở handoff trước.

## 5. Nghiệm thu — số đo thật

Trên Revit 2027, model `R27_Snowdon Towers Sample Architectural`, add-in `0.8.32` (`753eff2`, clean):

| Ca | Kết quả |
| --- | --- |
| dimension vào View3D | **400** `invalid_parameter`, message nêu lock không giúp |
| dimension vào FloorPlan | **ok** — không hồi quy |
| `value` / `valueMetres` | 5,2493 / 1,6 → tỉ số **3,28084** |
| `valueMetres` vs khoảng cách dựng sẵn | 1,6 m vs 1,60 m |
| chỉ 1 reference | **400** `bad_request` |
| thiếu `elementId` | **400** `invalid_parameter` (trước: 500) |

**Đối chứng, đúng cách các bạn đo** — trong plan view vừa tạo:

```
OST_Lines      : 2      (đối chứng: phép đo có năng lực phát hiện)
OST_Dimensions : 1      (Revit thấy dimension)
```

### Một cái bẫy khi tự kiểm, chúng tôi vấp phải

Lần chạy đầu, đối chứng của chúng tôi báo **0 dimension trong plan** — trông y như "plan cũng hỏng".
Thực ra là lỗi của phép đo: chúng tôi lấy level đầu danh sách (**"Parking", −5,16 m**) trong khi hình
học đặt ở **+2,51 m**, cách 7,7 m nên **ngoài view range**. Dựng lại trên **M1 (1,68 m)** với hình
học cao hơn level 1 m thì ra `2 lines / 1 dimension` đúng như bảng của các bạn.

Nêu ra vì nếu các bạn tái kiểm mà đặt chord ở cao độ xa level của plan view, sẽ thấy 0 và tưởng bản
sửa hỏng. `OST_Lines` phải > 0 trước đã — nếu nó cũng 0 thì đang đo view range chứ không đo dimension.

## 6. Phía các bạn cần làm gì

Không có gì bắt buộc. Hướng đã chốt ở mục 6 handoff của các bạn — dimension đặt trong plan
`spatial-qc QC - <level>`, view 3D chỉ để nhìn — **khớp hoàn toàn** với bản sửa này.

Hai điều nên biết khi triển khai:

1. Nếu code có nhánh nào từng gọi dimension vào view 3D (kể cả để thử), giờ nhận **400**, không còn
   `ok:true`. Fail rõ ràng, nhưng là thay đổi hành vi.
2. Khi đặt dimension trong plan, **cao độ của chord phải nằm trong view range của plan view đó** —
   xem mục 5. Đây là ràng buộc của Revit, không phải của add-in.

Đọc `valueMetres` thay vì tự nhân 0,3048 thì đỡ một chỗ có thể sai.

## 7. Không đổi

Envelope `{ok,data}`/`{ok,error}`, `/commands`, batch = 1 undo step, tool surface (**94** MCP tools,
101 lệnh C#, 10 ẩn) — tất cả nguyên vẹn. Lệnh này vốn đã là MCP tool, không đổi tên, không đổi
tham số.
