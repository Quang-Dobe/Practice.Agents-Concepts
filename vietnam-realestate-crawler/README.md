# Vietnam Real-Estate Report (< 2 tỷ) — HCM · Đà Nẵng · Khánh Hòa · Bình Dương · Vũng Tàu

Thu thập & đánh giá tin **nhà ở** và **đất thổ cư** giá **dưới 2 tỷ**, đăng trong
**vòng 1 năm**, tại 5 khu vực. Mỗi tin được chấm **độ uy tín**, **độ hợp lý giá** và
**độ phù hợp khi mua** theo một bộ tiêu chí thống nhất, trình bày trên site dark-theme.

## ⚠️ Giới hạn dữ liệu (quan trọng — trung thực)

Môi trường thực thi (Claude Code on the web) áp **network egress policy** chặn truy cập
trực tiếp mọi trang BĐS: `batdongsan.com.vn`, `nhatot.com`, `alonhadat.com.vn`… đều trả
`403` ở tầng CONNECT của proxy, và WebFetch cũng bị site chặn `403`. Kênh outbound **duy
nhất** được phép là **WebSearch**. Hệ quả:

- Dữ liệu ở **mức snippet** (kết quả tìm kiếm), không phải nội dung trang đầy đủ.
- **Không tải được ảnh** (sổ đỏ, ảnh nhà) ⇒ **không có phân tích ảnh** như mong muốn ban đầu.
- Một số trường (SĐT, mô tả đầy đủ) thường trống.

Điểm số vì thế mang tính **tham khảo**, phần lớn có `assessment_confidence` = *Trung bình/Thấp*.

## Cấu trúc

```
vietnam-realestate-crawler/
├── docs/
│   ├── PLAN.md           # kế hoạch + kiến trúc multi-agent
│   ├── METHODOLOGY.md    # rubric chấm điểm (hợp đồng chung cho agents)
│   └── SOURCES.md        # xếp hạng độ uy tín các trang nguồn
├── data/
│   ├── schema.json       # JSON schema cho dataset mỗi vùng
│   └── <region>.json     # dữ liệu do agents thu thập
├── site/                 # OUTPUT: mở site/index.html
│   ├── index.html        # tổng quan + bảng xếp hạng + link các vùng
│   ├── methodology.html  # phương pháp luận
│   ├── regions/*.html    # trang chi tiết từng vùng
│   └── assets/style.css  # dark theme
└── scripts/build_site.mjs  # generator (thuần Node, không phụ thuộc)
```

## Kiến trúc thực thi (multi-agent, token-managed)

- **Orchestrator (Opus 4.8)** — viết methodology/rubric, spawn agents, tổng hợp, khử
  trùng lặp liên vùng, build site. **Không dùng Fable 5** vì không verify được hạn mức
  tuần ⇒ tránh phát sinh usage credits.
- **6 agents thu thập (Sonnet 5)** — song song, mỗi agent một vùng, dùng WebSearch,
  lọc + chấm điểm theo `METHODOLOGY.md`, ghi `data/<region>.json`.

## Chạy lại

```bash
node scripts/build_site.mjs   # đọc data/*.json → sinh lại site/
```

Mở `site/index.html` bằng trình duyệt. Thêm/cập nhật dữ liệu bằng cách sửa các file
`data/<region>.json` theo `data/schema.json` rồi build lại.
