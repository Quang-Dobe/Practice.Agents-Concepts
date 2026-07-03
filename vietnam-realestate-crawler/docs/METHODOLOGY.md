# Phương pháp luận & Chỉ tiêu đánh giá

> Tài liệu này là **hợp đồng chung** cho mọi agent. Mọi điểm số phải tính theo đúng
> rubric dưới đây để kết quả giữa các vùng có thể so sánh với nhau.

## 0. Giới hạn dữ liệu (đọc trước tiên — bắt buộc minh bạch)

Môi trường thực thi **chặn truy cập trực tiếp** tới các trang BĐS (network egress
policy trả `403` ở CONNECT cho batdongsan.com.vn, nhatot.com, alonhadat.com.vn…).
Kênh outbound **duy nhất** là **WebSearch**. Do đó:

- Dữ liệu listing ở mức **snippet** (tiêu đề + đoạn mô tả ngắn do search trả về),
  **không** phải nội dung trang đầy đủ.
- **Không tải được ảnh** (sổ đỏ, ảnh nhà) → **không có phân tích ảnh**. Mọi trường
  ảnh để trống và ghi rõ `image_available: false`.
- Số điện thoại / tên môi giới thường không có trong snippet → thường trống.

Vì thế **mức độ tin cậy của đánh giá** (xem §4) của phần lớn listing sẽ là
**Trung bình** hoặc **Thấp**. Đây là giới hạn trung thực, không phải lỗi.

## 1. Phạm vi lọc (filter) — áp cho mọi listing

Một listing chỉ được giữ lại nếu thoả **tất cả**:

1. **Loại BĐS**: nhà ở (nhà riêng/nhà phố/nhà cấp 4) **hoặc** đất thổ cư (đất ở, ONT/ODT).
   Loại bỏ: căn hộ chung cư thuần, đất nông nghiệp, đất dự án chưa thổ cư, kho xưởng.
2. **Giá tổng < 2.000.000.000 VND** (2 tỷ). Nếu chỉ có "giá thoả thuận" → loại
   (không kiểm chứng được ngưỡng giá).
3. **Khu vực** thuộc: TP.HCM (mọi quận/TP.Thủ Đức), Đà Nẵng, Khánh Hòa,
   Bình Dương, Bà Rịa–Vũng Tàu.
4. **Thời điểm đăng ≤ 1 năm** (từ 2025-07 trở lại đây, mốc hôm nay 2026-07-03).
   Nếu không xác định được thời điểm → `posted_confidence: "unknown"` và hạ độ tin cậy.

## 2. Chống trùng lặp (deduplication)

Sinh **dedup_key** = chuẩn hoá(`đường/khu` + `phường/quận` + `diện tích làm tròn 1 m²`
+ `giá làm tròn 10 triệu`). Bỏ dấu, lowercase, bỏ ký tự thừa.

- Trùng `dedup_key` trên **cùng site** hoặc **khác site** → giữ **1 bản** (bản mô tả
  đầy đủ nhất), đánh dấu `duplicate_count` = số lần xuất hiện.
- `duplicate_count` cao (rao đi rao lại nhiều lần / nhiều tháng) là **tín hiệu xấu**
  (hàng khó bán, có thể vướng pháp lý) → trừ điểm độ phù hợp (§5).

## 3. Điểm ĐỘ UY TÍN của tin đăng (reliability_score) — thang 0..10

Tổng 4 nhóm tín hiệu (không có ảnh nên không chấm ảnh):

| Nhóm | Tối đa | Cách chấm |
|------|:------:|-----------|
| **A. Uy tín nguồn** | 3 | Site tier-1 (batdongsan, nhatot) = 3; tier-2 (homedy, cafeland, rever, muaban, alonhadat) = 2; tier-3/khác = 1 |
| **B. Đầy đủ thông tin** | 3 | +1 có diện tích cụ thể; +1 có địa chỉ tới đường/phường; +1 có nêu pháp lý (sổ đỏ/sổ hồng riêng) |
| **C. Hợp lý về giá** | 2 | Đơn giá (triệu/m²) nằm trong dải thị trường của khu vực = 2; lệch nhẹ = 1; lệch mạnh/"quá rẻ bất thường" = 0 |
| **D. Tươi & không rao lại** | 2 | +1 đăng trong 12 tháng và xác định được; +1 `duplicate_count == 1` |

Làm tròn 1 chữ số thập phân. Ghi `reliability_breakdown` cho từng nhóm A/B/C/D.

## 4. MỨC ĐỘ TIN CẬY của đánh giá (assessment_confidence) — nhãn

Khác với §3 (chấm bản thân tin đăng), đây là **độ tin cậy vào chính đánh giá của ta**,
do dữ liệu chỉ ở mức snippet:

- **Cao**: có đủ diện tích + giá + địa chỉ + pháp lý + khớp dải giá thị trường.
- **Trung bình**: thiếu 1–2 trong các yếu tố trên nhưng vẫn suy luận được.
- **Thấp**: thiếu nhiều, hoặc giá/diện tích mơ hồ, hoặc không xác định thời điểm.

## 5. Điểm ĐỘ PHÙ HỢP KHI MUA (suitability_score) — thang 0..10 (điểm headline)

Công thức tổng hợp (rồi làm tròn 1 số lẻ, kẹp 0..10):

```
suitability = 0.40 * reliability_score
            + 0.25 * price_fit_points_scaled   // §6, quy về 0..10
            + 0.20 * legal_clarity_scaled       // sổ riêng/thổ cư rõ = 10, sổ chung = 4, mập mờ = 2
            + 0.15 * location_desirability       // theo dải giá & vị trí khu (0..10, agent ước lượng có nêu lý do)
            - penalty_duplicate                  // duplicate_count>=3: -1.0 ; >=5: -2.0
```

Kèm `suitability_reason`: 1–2 câu giải thích **vì sao** điểm đó (bắt buộc, không để trống).

## 6. Đánh giá HỢP LÝ VỀ GIÁ (price_fit) — so với mặt bằng khu vực

- Tính **đơn giá** = giá_tổng / diện_tích (triệu/m²).
- So với **dải giá thị trường** của quận/khu (agent thu từ WebSearch các bài "báo cáo giá",
  "mặt bằng giá", lưu vào `market_bands`).
- Kết luận `price_fit`: `"tốt (dưới mặt bằng)"` | `"hợp lý (đúng mặt bằng)"` |
  `"cao hơn mặt bằng"` | `"rẻ bất thường — cần cảnh giác"`.
- **Cảnh báo**: đơn giá thấp hơn ~40% dải thị trường thường là dấu hiệu **lừa đảo /
  vướng pháp lý / sổ chung** → `price_fit = "rẻ bất thường"`, hạ độ phù hợp.

## 7. Đầu ra bắt buộc mỗi vùng

Ghi `data/<region-slug>.json` theo `data/schema.json`, gồm:
`region`, `market_bands[]` (dải giá theo quận/khu + nguồn), `listings[]` (đã lọc & dedup),
`sources_used[]`, `notes`.
