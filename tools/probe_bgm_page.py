# -*- coding: utf-8 -*-
"""抓 bgm.tv 角色网页，找「简体中文名」标签的渲染来源。用法: python probe_bgm_page.py <char_id>"""
import re
import sys
import urllib.request

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

cid = sys.argv[1] if len(sys.argv) > 1 else "211740"
req = urllib.request.Request(
    f"https://bgm.tv/character/{cid}",
    headers={"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"},
)
html = urllib.request.urlopen(req, timeout=15).read().decode("utf-8", errors="replace")

print(f"=== 简体中文名 相关片段 (char {cid}) ===")
found = False
for m in re.finditer(r".{60}简体中文名.{180}", html, re.S):
    found = True
    print("---", m.group(0)[:240].replace("\n", " "))
if not found:
    print("(页面无「简体中文名」字样)")

print("\n=== 页面标题/名称区 ===")
m = re.search(r"<h1[^>]*>(.*?)</h1>", html, re.S)
if m:
    print("h1:", re.sub(r"<[^>]+>", "", m.group(1)).strip())
for m in re.finditer(r'class="name"[^>]*>(.*?)<', html, re.S):
    print("name:", re.sub(r"<[^>]+>", "", m.group(1)).strip()[:80])
