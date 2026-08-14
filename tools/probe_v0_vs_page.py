# -*- coding: utf-8 -*-
"""
验证：v0/characters/{id} 的 name 字段是否 == 页面「简体中文名」（有标签时）。
一致 → 用 v0 API 即可实现「日文名→简体中文名」，无需抓 HTML。
"""
import json
import re
import sys
import urllib.request

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

API_UA = "PotatoVN.LibraryPlus/1.0 (plugin)"
PAGE_UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PotatoVN.LibraryPlus/1.0"
PAT = re.compile(r'<span class="tip">简体中文名: </span>([^<]+)')

ids = [int(a) for a in sys.argv[1:]] if len(sys.argv) > 1 else [48185, 74097, 74098, 74099, 48436]

for cid in ids:
    try:
        req = urllib.request.Request(f"https://api.bgm.tv/v0/characters/{cid}", headers={"User-Agent": API_UA})
        d = json.loads(urllib.request.urlopen(req, timeout=15).read().decode("utf-8"))
        v0_name = d.get("name")
    except Exception as e:
        v0_name = f"ERR {e}"
    try:
        req = urllib.request.Request(f"https://bgm.tv/character/{cid}", headers={"User-Agent": PAGE_UA})
        html = urllib.request.urlopen(req, timeout=15).read().decode("utf-8", errors="replace")
        m = PAT.search(html)
        page_cn = m.group(1).strip() if m else None
    except Exception as e:
        page_cn = f"ERR {e}"
    same = "==" if v0_name == page_cn else ("!=" if not str(v0_name).startswith("ERR") and not str(page_cn).startswith("ERR") else "?")
    print(f"id={cid} v0.name={v0_name!r} 页面简体中文名={page_cn!r} {same}")
