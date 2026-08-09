# Handoff liên repo

Quy ước: thay đổi ảnh hưởng repo khác → viết handoff mô tả **triệu chứng + cách sửa** để repo đích
tự thực thi. Không tự sửa, không tự push, không ghi file vào repo họ.
Nguồn quy ước: [`CROSS_REPO_ACTIONS_2026-07-17.md`](../CROSS_REPO_ACTIONS_2026-07-17.md) và
[`docs/DOWNSTREAM_CONTRACTS.md`](DOWNSTREAM_CONTRACTS.md).

## Handoff đang mở

- [AutomatedSpatialQC — `spatial_get_paths_of_travel` đã ship ở v0.8.24](handoffs/HANDOFF_spatialqc_paths_of_travel_shipped.md)
  — phản hồi `HANDOFF_get_paths_of_travel.md`; contract đúng nguyên bản, consumer không cần sửa.
  Kèm đáp án 2 câu hỏi mở về API/BuiltInParameter (dùng lại được cho `create_path_of_travel`) và
  cảnh báo `levelName` là tên tầng Revit, có thể không khớp storey của `GridSet`.
- [bim-orchestrator — `configure_schedule` numeric filter đã đóng ở v0.8.23](handoffs/HANDOFF_bimorch_schedule_filter_numeric_closed.md)
  — phản hồi handoff của họ; không cần họ sửa gì để hưởng fix, chỉ có dọn dẹp tuỳ chọn
  (`tests/_mocks.py` đang chặt hơn addin thật).
- [RevitAssistant — kernel đổi ở v0.8.23, cân nhắc re-sync `extern/RevitMCPCore`](handoffs/HANDOFF_revitassistant_kernel_resync_v0823.md)
  — đang pin v0.8.19, cách 10 commit; v0.8.23 chưa có tag.
