# Kế hoạch thu thập & đánh giá nhà đất (< 2 tỷ, 5 vùng)

## Mục tiêu
Thu thập tin **nhà ở** + **đất thổ cư** giá **< 2 tỷ**, đăng trong **≤ 1 năm**, tại
TP.HCM (mọi quận), Đà Nẵng, Khánh Hòa, Bình Dương, Bà Rịa–Vũng Tàu. Với mỗi tin: giá
tổng, diện tích, đơn giá, **báo cáo mặt bằng giá khu vực/trục đường**, và 3 chỉ số:
**độ uy tín /10**, **độ hợp lý giá**, **độ phù hợp mua /10**, kèm **mức độ tin cậy đánh giá**.

## Ràng buộc môi trường (đã kiểm chứng)
Egress policy chặn mọi site BĐS (403 CONNECT). Kênh duy nhất = **WebSearch**. ⇒ Dữ liệu
mức snippet, **không có ảnh**, không phân tích ảnh. Xem METHODOLOGY §0.

## Kiến trúc thực thi (token-managed)
- **Orchestrator = Opus (main loop)**: viết methodology/rubric/sources, spawn agents,
  tổng hợp, dedup liên vùng, build HTML, commit/push. Không dùng **Fable 5** (không
  verify được quota tuần ⇒ tránh usage credits).
- **6 agents thu thập = Sonnet 5** (WebSearch-heavy, song song, chi phí hợp lý):

| Agent | Vùng | data file |
|------|------|-----------|
| 1 | HCM — quận trung tâm (Q1,3,4,5,6,8,10,11, Bình Thạnh, Phú Nhuận, Tân Bình) | `data/hcm-trung-tam.json` |
| 2 | HCM — quận ven & Thủ Đức (Q7,12, Tân Phú, Gò Vấp, Bình Tân, TP Thủ Đức, Bình Chánh, Hóc Môn, Củ Chi, Nhà Bè) | `data/hcm-ven.json` |
| 3 | Đà Nẵng (Hải Châu, Thanh Khê, Liên Chiểu, Cẩm Lệ, Sơn Trà, Ngũ Hành Sơn, Hòa Vang) | `data/da-nang.json` |
| 4 | Khánh Hòa (Nha Trang, Cam Ranh, Ninh Hòa, Diên Khánh, Cam Lâm) | `data/khanh-hoa.json` |
| 5 | Bình Dương (Thủ Dầu Một, Dĩ An, Thuận An, Bến Cát, Tân Uyên) | `data/binh-duong.json` |
| 6 | Bà Rịa–Vũng Tàu (Vũng Tàu, Bà Rịa, Phú Mỹ, Long Điền, Đất Đỏ, Châu Đức) | `data/vung-tau.json` |

## Các bước
1. ✅ Scaffold + METHODOLOGY + SOURCES + schema (orchestrator).
2. ⏳ Spawn 6 agents Sonnet song song → mỗi agent: WebSearch theo quận/khu, trích data,
   lọc (<2 tỷ, nhà/đất thổ cư, ≤1 năm), dedup nội vùng, chấm điểm theo rubric, ghi JSON.
3. Orchestrator: gộp, **dedup liên vùng**, kiểm tra tính hợp lệ theo schema.
4. Build **site dark-theme**: `index.html` (tổng quan + bảng xếp hạng) → `regions/*.html`
   (chi tiết từng vùng) + `methodology.html`.
5. Commit + push nhánh `claude/vietnam-realestate-crawler-nm6oax` + mở draft PR.

## Không làm (trung thực)
- Không lách egress policy. Không tải/nhúng ảnh. Không bịa listing/giá.
- Không spawn Fable 5.
