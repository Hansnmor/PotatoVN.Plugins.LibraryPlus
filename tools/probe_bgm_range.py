# -*- coding: utf-8 -*-
"""
实现前验证：
1) bgm.tv 角色页是否支持 Range 头（只取前 100KB，名称区在页面头部）
2) 纯日文名角色（2543 オトフリート4世）是否有「简体中文名」
3) 抽查几个 kungal 验证时见过的角色 id（从 kungal links 拿的 bgm id）
用法: python probe_bgm_range.py [id ...]
"""
import re
import sys
import urllib.request
import urllib.error

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PotatoVN.LibraryPlus/1.0"


def fetch(cid, with_range=True):
    headers = {"User-Agent": UA}
    if with_range:
        headers["Range"] = "bytes=0-120000"
    req = urllib.request.Request(f"https://bgm.tv/character/{cid}", headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            return r.status, r.headers.get("Content-Range"), r.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        return e.code, None, e.read().decode("utf-8", errors="replace")[:100]
    except Exception as e:
        return -1, None, str(e)


PAT = re.compile(r'<span class="tip">简体中文名: </span>([^<]+)')

for cid in sys.argv[1:] or ["2543", "211740", "84"]:
    status, cr, body = fetch(cid, with_range=True)
    print(f"[{status}] id={cid} Content-Range={cr} len={len(body)}")
    m = PAT.search(body)
    if m:
        print(f"    简体中文名 = {m.group(1).strip()}")
    else:
        print("    (前 120KB 内无「简体中文名」)")
    print()
