# -*- coding: utf-8 -*-
"""打印 bgm.tv 角色页 h1 →「简体中文名」之间的 HTML，确定父容器。"""
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

i = html.find("<h1")
j = html.find("简体中文名")
print(re.sub(r"\s+", " ", html[i:i + (j - i) + 60]))
