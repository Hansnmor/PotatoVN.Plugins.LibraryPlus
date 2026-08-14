# -*- coding: utf-8 -*-
"""打印 bgm.tv 角色页名称区完整结构（h1 + ul.name），对照有/无「简体中文名」的页面。"""
import re
import sys
import urllib.request

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

for cid in sys.argv[1:] or ["84", "211740"]:
    req = urllib.request.Request(
        f"https://bgm.tv/character/{cid}",
        headers={"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"},
    )
    html = urllib.request.urlopen(req, timeout=15).read().decode("utf-8", errors="replace")
    print(f"===== char {cid} =====")
    m = re.search(r"<h1[^>]*>(.*?)</h1>", html, re.S)
    print("h1:", re.sub(r"<[^>]+>", "", m.group(1)).strip() if m else "(none)")
    m = re.search(r'<ul class="name"[^>]*>(.*?)</ul>', html, re.S)
    if m:
        ul = m.group(1)
        for li in re.findall(r"<li[^>]*>(.*?)</li>", ul, re.S):
            text = re.sub(r"<[^>]+>", "", li)
            text = re.sub(r"\s+", " ", text).strip()
            print("  li:", text[:100])
    else:
        print("  (无 ul.name)")
    print()
