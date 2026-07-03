# Nguồn dữ liệu & Đánh giá độ uy tín trang

Xếp hạng theo uy tín/độ phủ để dùng trong nhóm **A** của rubric (METHODOLOGY §3).

## Tier 1 — phủ rộng, chuẩn hoá tốt, uy tín cao (A = 3 điểm)

| Site | Ghi chú |
|------|---------|
| **batdongsan.com.vn** | Lớn nhất VN, dữ liệu chuẩn hoá theo quận/giá/diện tích, có báo cáo giá theo khu. |
| **nhatot.com** (Chợ Tốt Nhà) | Thanh khoản cao, tin cá nhân nhiều, có bộ lọc giá theo bậc (1–2 tỷ…). |

## Tier 2 — uy tín khá, phủ tốt (A = 2 điểm)

| Site | Ghi chú |
|------|---------|
| **homedy.com** | Tổng hợp tốt, có trang theo dải giá. |
| **cafeland.vn** / nhadat.cafeland.vn | Có mảng tin tức + báo cáo thị trường (tốt cho `market_bands`). |
| **rever.vn** | Môi giới chuyên nghiệp, dữ liệu sạch, thiên HCM. |
| **alonhadat.com.vn** | Lâu đời, bộ lọc "đất thổ cư/đất ở" rõ ràng. |
| **muaban.net** | Rao vặt lớn, tin cá nhân. |
| **nhadat24h.net**, **muonnha.com.vn**, **bds68.com.vn** | Tổng hợp theo dải giá. |

## Tier 3 — dùng bổ sung, hạ độ tin cậy (A = 1 điểm)

Diễn đàn, group, tin lẻ không rõ nguồn. Chỉ dùng khi Tier 1–2 không có, và luôn hạ
`assessment_confidence`.

## Nguồn cho mặt bằng giá (`market_bands`)

Ưu tiên các bài "**báo cáo giá**", "**mặt bằng giá m²**", "**giá đất [quận] 2025/2026**"
từ batdongsan.com.vn (mục thống kê), cafeland, và báo chí (cafef, vnexpress bất động sản).
Ghi rõ `source_url` cho từng dải giá.

## Nguyên tắc trích dẫn

Mọi listing và mọi dải giá **phải** có `source_url`. Không bịa số liệu. Nếu snippet
không cho đủ thông tin, để trống trường đó thay vì suy đoán.
