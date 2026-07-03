#!/usr/bin/env node
// Static dark-theme site generator for the VN real-estate report.
// Reads data/*.json (schema.json excluded), cross-region dedups, emits site/.
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(path.dirname(new URL(import.meta.url).pathname), '..');
const DATA = path.join(ROOT, 'data');
const SITE = path.join(ROOT, 'site');
const REGIONS_DIR = path.join(SITE, 'regions');
fs.mkdirSync(REGIONS_DIR, { recursive: true });

const esc = (s) => String(s ?? '').replace(/[&<>"]/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;' }[c]));
const fmtVnd = (n) => {
  if (n == null || isNaN(n)) return '—';
  const ty = Math.floor(n / 1e9), tr = Math.round((n % 1e9) / 1e6);
  if (ty > 0) return tr > 0 ? `${ty} tỷ ${tr} triệu` : `${ty} tỷ`;
  return `${Math.round(n / 1e6)} triệu`;
};
const scoreClass = (s) => s >= 7 ? 's-good' : s >= 5 ? 's-mid' : 's-bad';
const confClass = (c) => c === 'Cao' ? 'conf-Cao' : c === 'Thấp' ? 'conf-Thap' : 'conf-TB';
const fx = (n) => (n == null || isNaN(n)) ? '—' : (Math.round(n * 10) / 10).toFixed(1);

// ---- load datasets ----
let datasets = [];
if (fs.existsSync(DATA)) {
  for (const f of fs.readdirSync(DATA)) {
    if (!f.endsWith('.json') || f === 'schema.json') continue;
    try {
      const d = JSON.parse(fs.readFileSync(path.join(DATA, f), 'utf8'));
      if (d && Array.isArray(d.listings)) { d.__file = f; datasets.push(d); }
      else console.warn(`skip ${f}: no listings[]`);
    } catch (e) { console.warn(`skip ${f}: ${e.message}`); }
  }
}
datasets.sort((a, b) => (a.region || '').localeCompare(b.region || '', 'vi'));

// ---- cross-region dedup (keep highest reliability per dedup_key) ----
const seen = new Map();
let crossDupes = 0;
for (const d of datasets) {
  for (const l of d.listings) {
    l.__region = d.region; l.__slug = d.region_slug;
    const k = (l.dedup_key || `${l.district}|${l.area_m2}|${l.total_price_vnd}`).toLowerCase();
    if (seen.has(k)) {
      crossDupes++;
      const prev = seen.get(k);
      prev.duplicate_count = (prev.duplicate_count || 1) + (l.duplicate_count || 1);
      l.__hidden = true;
    } else seen.set(k, l);
  }
}
const allVisible = [...seen.values()];
const totalListings = allVisible.length;
const avgSuit = totalListings ? allVisible.reduce((s, l) => s + (l.suitability_score || 0), 0) / totalListings : 0;
const withLegal = allVisible.filter(l => l.legal_status && /sổ (đỏ|hồng) riêng|thổ cư|sổ riêng/i.test(l.legal_status)).length;
const topPicks = [...allVisible].sort((a, b) => (b.suitability_score || 0) - (a.suitability_score || 0)).slice(0, 15);

const shell = (title, bodyHtml, depth = 0) => {
  const base = depth === 0 ? '.' : '..';
  return `<!doctype html><html lang="vi"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${esc(title)}</title>
<link rel="stylesheet" href="${base}/assets/style.css">
</head><body>${bodyHtml}
<footer class="site"><div class="wrap">Báo cáo tạo tự động bằng dữ liệu WebSearch (mức snippet). Không phải lời khuyên đầu tư. Luôn kiểm tra pháp lý & xem nhà trực tiếp trước khi giao dịch.</div></footer>
</body></html>`;
};

const listingCard = (l) => {
  const dupChip = (l.duplicate_count || 1) >= 3 ? `<span class="chip dup">Rao lại ${l.duplicate_count}×</span>` : '';
  const legalChip = l.legal_status ? `<span class="chip legal">${esc(l.legal_status)}</span>` : '';
  const confKey = l.assessment_confidence === 'Cao' ? 'Cao' : l.assessment_confidence === 'Thấp' ? 'Thap' : 'TB';
  return `<div class="card listing"
    data-suit="${l.suitability_score||0}" data-rel="${l.reliability_score||0}"
    data-price="${l.total_price_vnd||0}" data-area="${l.area_m2||0}" data-type="${esc(l.property_type||'')}">
  <p class="l-title">${esc(l.title || 'Tin đăng')}</p>
  <div class="row"><span class="price">${esc(l.total_price_label || fmtVnd(l.total_price_vnd))}</span>
    <span class="kv">Diện tích <b>${esc(l.area_m2)} m²</b></span>
    <span class="kv">Đơn giá <b>${l.price_per_m2_million != null ? esc(fx(l.price_per_m2_million)) + ' tr/m²' : '—'}</b></span></div>
  <div class="row"><span class="kv">📍 <b>${esc(l.district || '')}${l.ward_or_street ? ' — ' + esc(l.ward_or_street) : ''}</b></span></div>
  <div class="chips">
    <span class="chip">${l.property_type === 'dat_tho_cu' ? 'Đất thổ cư' : 'Nhà ở'}</span>
    ${legalChip}${dupChip}
    ${l.posted_period ? `<span class="chip">Đăng ${esc(l.posted_period)}</span>` : ''}
    <span class="chip">Hợp lý giá: ${esc(l.price_fit || '—')}</span>
  </div>
  <div class="scores">
    <div class="score"><div class="sv ${scoreClass(l.suitability_score||0)}">${fx(l.suitability_score)}</div><div class="sl">Phù hợp mua</div></div>
    <div class="score"><div class="sv ${scoreClass(l.reliability_score||0)}">${fx(l.reliability_score)}</div><div class="sl">Độ uy tín</div></div>
    <div class="score"><div class="sl" style="margin-top:6px">Tin cậy ĐG</div><div><span class="conf conf-${confKey}">${esc(l.assessment_confidence||'—')}</span></div></div>
  </div>
  ${l.suitability_reason ? `<div class="reason">“${esc(l.suitability_reason)}”</div>` : ''}
  <div class="noimg">🖼️ Không có ảnh (sổ đỏ/nhà) — môi trường chặn tải ảnh; không phân tích được hình.</div>
  <div class="src">Nguồn: <b>${esc(l.source_site||'')}</b> · <a href="${esc(l.source_url||'#')}" target="_blank" rel="noopener">Xem tin gốc ↗</a></div>
</div>`;
};

// ---- region pages ----
for (const d of datasets) {
  const visible = d.listings.filter(l => !l.__hidden);
  const bands = (d.market_bands || []).map(b => `<tr>
    <td>${esc(b.area_name)}</td>
    <td>${b.price_per_m2_low != null ? esc(b.price_per_m2_low) : '—'}${b.price_per_m2_high != null ? '–' + esc(b.price_per_m2_high) : ''} ${b.price_per_m2_low != null ? 'tr/m²' : ''}</td>
    <td>${esc(b.price_per_m2_note || '')}</td>
    <td>${esc(b.commentary || '')}</td>
    <td><a href="${esc(b.source_url||'#')}" target="_blank" rel="noopener">nguồn ↗</a></td></tr>`).join('');
  const body = `<header class="site"><div class="wrap">
    <div class="crumbs"><a href="../index.html">← Trang tổng quan</a></div>
    <h1>${esc(d.region)}</h1>
    <p class="sub">${esc(d.generated_note || '')} — ${visible.length} tin (đã lọc &lt;2 tỷ, nhà/đất thổ cư, ≤1 năm, đã khử trùng lặp).</p>
  </div></header>
  <div class="wrap">
    <div class="banner"><strong>Giới hạn dữ liệu:</strong> dữ liệu ở mức snippet từ WebSearch, <strong>không có ảnh</strong> (sổ đỏ/nhà) nên không phân tích hình ảnh; một số trường có thể trống. Điểm số mang tính tham khảo.</div>
    ${bands ? `<h2>Mặt bằng giá khu vực (báo cáo giá)</h2><div class="tbl-scroll"><table class="tbl">
      <thead><tr><th>Khu vực / trục đường</th><th>Đơn giá</th><th>Ghi chú</th><th>Nhận định</th><th>Nguồn</th></tr></thead>
      <tbody>${bands}</tbody></table></div>` : ''}
    <h2>Danh sách tin (${visible.length})</h2>
    <div class="toolbar">
      <label class="kv">Sắp xếp:
      <select id="sort" onchange="sortCards()">
        <option value="suit">Độ phù hợp mua ↓</option>
        <option value="rel">Độ uy tín ↓</option>
        <option value="price">Giá ↑</option>
        <option value="area">Diện tích ↓</option>
      </select></label>
      <label class="kv">Loại:
      <select id="ftype" onchange="sortCards()">
        <option value="">Tất cả</option><option value="nha_o">Nhà ở</option><option value="dat_tho_cu">Đất thổ cư</option>
      </select></label>
    </div>
    <div class="grid" id="cards">${visible.map(listingCard).join('\n')}</div>
  </div>
  <script>
  function sortCards(){
    const key=document.getElementById('sort').value, ft=document.getElementById('ftype').value;
    const g=document.getElementById('cards'); const cards=[...g.children];
    cards.forEach(c=>{ c.style.display = (!ft||c.dataset.type===ft)?'':'none'; });
    const asc = key==='price';
    cards.sort((a,b)=>{ const av=+a.dataset[key==='suit'?'suit':key==='rel'?'rel':key], bv=+b.dataset[key==='suit'?'suit':key==='rel'?'rel':key]; return asc?av-bv:bv-av; });
    cards.forEach(c=>g.appendChild(c));
  }
  </script>`;
  fs.writeFileSync(path.join(REGIONS_DIR, `${d.region_slug}.html`), shell(d.region + ' — Nhà đất <2 tỷ', body, 1));
}

// ---- index ----
const regionCards = datasets.map(d => {
  const vis = d.listings.filter(l => !l.__hidden);
  const avg = vis.length ? (vis.reduce((s, l) => s + (l.suitability_score || 0), 0) / vis.length) : 0;
  const best = vis.length ? Math.max(...vis.map(l => l.suitability_score || 0)) : 0;
  return `<a class="card region-card" href="regions/${esc(d.region_slug)}.html">
    <div class="rc-title">${esc(d.region)}</div>
    <div class="rc-meta">${vis.length} tin · ${(d.market_bands||[]).length} dải giá · phù hợp TB <b class="${scoreClass(avg)}">${fx(avg)}</b> · cao nhất <b class="${scoreClass(best)}">${fx(best)}</b></div>
  </a>`;
}).join('\n');

const topRows = topPicks.map((l, i) => `<tr>
  <td>${i + 1}</td>
  <td>${esc(l.district || '')} <span class="kv">(${esc((l.__region || '').split(' (')[0])})</span></td>
  <td>${esc(l.property_type === 'dat_tho_cu' ? 'Đất thổ cư' : 'Nhà ở')}</td>
  <td>${esc(l.total_price_label || fmtVnd(l.total_price_vnd))}</td>
  <td>${esc(l.area_m2)} m²</td>
  <td>${l.price_per_m2_million != null ? fx(l.price_per_m2_million) + ' tr/m²' : '—'}</td>
  <td class="${scoreClass(l.reliability_score||0)}"><b>${fx(l.reliability_score)}</b></td>
  <td class="${scoreClass(l.suitability_score||0)}"><b>${fx(l.suitability_score)}</b></td>
  <td><span class="conf ${confClass(l.assessment_confidence)}">${esc(l.assessment_confidence||'—')}</span></td>
  <td><a href="${esc(l.source_url||'#')}" target="_blank" rel="noopener">tin ↗</a></td>
</tr>`).join('');

const indexBody = `<header class="site"><div class="wrap">
  <h1>Báo cáo nhà đất &lt; 2 tỷ — TP.HCM, Đà Nẵng, Khánh Hòa, Bình Dương, Vũng Tàu</h1>
  <p class="sub">Nhà ở &amp; đất thổ cư, giá tổng dưới 2 tỷ, tin đăng trong vòng 1 năm. Mỗi tin được chấm <b>độ uy tín</b>, <b>độ hợp lý giá</b> và <b>độ phù hợp khi mua</b> theo một bộ tiêu chí thống nhất. <a href="methodology.html">Xem phương pháp luận →</a></p>
</div></header>
<div class="wrap">
  <div class="banner"><strong>⚠️ Giới hạn dữ liệu quan trọng:</strong> môi trường thực thi chặn truy cập trực tiếp tới các trang BĐS (network policy 403), nên dữ liệu được thu <strong>qua WebSearch ở mức snippet</strong>, <strong>KHÔNG tải được ảnh</strong> (sổ đỏ, ảnh nhà) và <strong>không phân tích ảnh</strong>. Nhiều tin thiếu SĐT/mô tả đầy đủ. Điểm số là <strong>tham khảo</strong>, không thay thế việc kiểm tra pháp lý &amp; xem nhà trực tiếp.</div>
  <div class="stats">
    <div class="stat"><div class="n">${totalListings}</div><div class="l">Tin (sau khử trùng lặp)</div></div>
    <div class="stat"><div class="n">${datasets.length}</div><div class="l">Vùng khảo sát</div></div>
    <div class="stat"><div class="n ${scoreClass(avgSuit)}">${fx(avgSuit)}</div><div class="l">Độ phù hợp mua TB /10</div></div>
    <div class="stat"><div class="n">${withLegal}</div><div class="l">Tin nêu sổ riêng/thổ cư</div></div>
    <div class="stat"><div class="n">${crossDupes}</div><div class="l">Bản trùng đã gộp</div></div>
  </div>
  <h2>🏆 Top tin phù hợp nhất để mua</h2>
  <div class="tbl-scroll"><table class="tbl">
    <thead><tr><th>#</th><th>Khu vực</th><th>Loại</th><th>Giá</th><th>DT</th><th>Đơn giá</th><th>Uy tín</th><th>Phù hợp</th><th>Tin cậy</th><th></th></tr></thead>
    <tbody>${topRows || '<tr><td colspan="10" class="kv">Chưa có dữ liệu.</td></tr>'}</tbody>
  </table></div>
  <h2>Khu vực</h2>
  <div class="grid">${regionCards || '<p class="kv">Chưa có dữ liệu vùng.</p>'}</div>
</div>`;
fs.writeFileSync(path.join(SITE, 'index.html'), shell('Báo cáo nhà đất <2 tỷ — 5 khu vực', indexBody, 0));

// ---- methodology page (light markdown render) ----
function mdToHtml(md) {
  const lines = md.split('\n'); let html = '', inTable = false, inUl = false, inCode = false;
  const inline = (t) => esc(t).replace(/`([^`]+)`/g, '<code>$1</code>').replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
  for (let raw of lines) {
    if (raw.startsWith('```')) { inCode = !inCode; html += inCode ? '<pre>' : '</pre>'; continue; }
    if (inCode) { html += esc(raw) + '\n'; continue; }
    if (/^\|/.test(raw)) {
      const cells = raw.split('|').slice(1, -1).map(c => c.trim());
      if (/^[\s|:-]+$/.test(raw)) continue;
      if (!inTable) { html += '<div class="tbl-scroll"><table class="tbl"><tbody>'; inTable = true; }
      html += '<tr>' + cells.map(c => `<td>${inline(c)}</td>`).join('') + '</tr>'; continue;
    } else if (inTable) { html += '</tbody></table></div>'; inTable = false; }
    if (/^\s*[-*] /.test(raw)) { if (!inUl) { html += '<ul>'; inUl = true; } html += `<li>${inline(raw.replace(/^\s*[-*] /, ''))}</li>`; continue; }
    else if (inUl) { html += '</ul>'; inUl = false; }
    if (/^### /.test(raw)) html += `<h3>${inline(raw.slice(4))}</h3>`;
    else if (/^## /.test(raw)) html += `<h2>${inline(raw.slice(3))}</h2>`;
    else if (/^# /.test(raw)) html += `<h1>${inline(raw.slice(2))}</h1>`;
    else if (/^> /.test(raw)) html += `<div class="banner">${inline(raw.slice(2))}</div>`;
    else if (raw.trim() === '') html += '';
    else html += `<p>${inline(raw)}</p>`;
  }
  if (inTable) html += '</tbody></table></div>'; if (inUl) html += '</ul>'; if (inCode) html += '</pre>';
  return html;
}
let methoBody = '<header class="site"><div class="wrap"><div class="crumbs"><a href="index.html">← Trang tổng quan</a></div><h1>Phương pháp luận &amp; Tiêu chí đánh giá</h1></div></header><div class="wrap">';
try { methoBody += mdToHtml(fs.readFileSync(path.join(ROOT, 'docs', 'METHODOLOGY.md'), 'utf8')); }
catch { methoBody += '<p class="kv">Chưa có METHODOLOGY.md</p>'; }
methoBody += '</div>';
fs.writeFileSync(path.join(SITE, 'methodology.html'), shell('Phương pháp luận', methoBody, 0));

console.log(`Built: ${datasets.length} regions, ${totalListings} listings, ${crossDupes} cross-region dupes merged.`);
