# -*- coding: utf-8 -*-
"""打印 bgm.tv 角色页「简体中文名」标签前后的完整 HTML 结构。"""
import re
import sys
import urllib.request

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

cid = sys.argv[1] if len(sys.argv) > 1 else "84"
req = urllib.request.Request(
    f"https://bgm.tv/character/{cid}",
    headers={"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"},
)
html = urllib.request.urlopen(req, timeout=15).read().decode("utf-8", errors="replace")

m = re.search(r".{400}简体中文名.{200}", html, re.S)
if m:
    print(m.group(0).replace("\n", " "))
else:
    print("无「简体中文名」")
    # 打印 h1 后 800 字符看结构
    i = html.find("<h1")
    print("h1 后区域:", re.sub(r"\s+", " ", html[i:i + 800]))
