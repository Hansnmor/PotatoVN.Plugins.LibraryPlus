# -*- coding: utf-8 -*-
"""
对比 Bangumi 角色接口：v0 端点 vs 旧版端点，找 name_cn 字段。
用法: python probe_bgm_char2.py [id1 id2 ...]
"""
import json
import sys
import urllib.request
import urllib.error

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

UA = "PotatoVN.LibraryPlus/1.0 (plugin)"
BASES = {
    "v0": "https://api.bgm.tv/v0/characters",
    "legacy": "https://api.bgm.tv/character",
}


def get(url):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            return r.status, json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", errors="replace")[:150]
    except Exception as e:
        return -1, str(e)


ids = [int(a) for a in sys.argv[1:]] if len(sys.argv) > 1 else [211740, 2543, 84]

for cid in ids:
    for label, base in BASES.items():
        status, d = get(f"{base}/{cid}")
        if status != 200:
            print(f"[{label}] id={cid} FAIL: {str(d)[:100]}")
            continue
        keys = sorted(d.keys())
        cn = d.get("name_cn") if isinstance(d, dict) else None
        print(f"[{label}] id={cid} name={d.get('name')!r} name_cn={cn!r}")
        if label == "legacy":
            print(f"      keys={keys}")
    print()
