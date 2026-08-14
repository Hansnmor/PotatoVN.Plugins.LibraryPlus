# -*- coding: utf-8 -*-
"""Dump 单个角色完整字段（验证角色简介来源）。用法: python dump_char.py <char_id>"""
import json
import sys
import urllib.request

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

char_id = sys.argv[1] if len(sys.argv) > 1 else "170426"
BASE = "https://www.kungal.com/api"
COOKIE = 'KUNGalgameSettings={"showKUNGalgameContentLimit":"all"}'
UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"

req = urllib.request.Request(
    BASE + f"/galgame-character/{char_id}",
    headers={"User-Agent": UA, "Cookie": COOKIE},
)
d = json.loads(urllib.request.urlopen(req, timeout=15).read().decode("utf-8"))["data"]
print("KEYS:", sorted(d.keys()))
print()
for k, v in d.items():
    s = json.dumps(v, ensure_ascii=False)
    print(f"{k}: {s[:500]}")
