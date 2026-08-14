# -*- coding: utf-8 -*-
"""
测试 Bangumi API 版本协商：X-Api-Version: 2 是否让 /v0/characters 返回 name_cn。
用法: python probe_bgm_v2.py [char_id ...]
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
ids = [int(a) for a in sys.argv[1:]] if len(sys.argv) > 1 else [211740, 84, 2543]

for cid in ids:
    for ver in ("1", "2"):
        req = urllib.request.Request(
            f"https://api.bgm.tv/v0/characters/{cid}",
            headers={"User-Agent": UA, "X-Api-Version": ver},
        )
        try:
            with urllib.request.urlopen(req, timeout=15) as r:
                d = json.loads(r.read().decode("utf-8"))
            print(f"[v{ver}] id={cid} name={d.get('name')!r} name_cn={d.get('name_cn')!r}")
        except urllib.error.HTTPError as e:
            print(f"[v{ver}] id={cid} HTTP {e.code}: {e.read().decode('utf-8', errors='replace')[:100]}")
        except Exception as e:
            print(f"[v{ver}] id={cid} ERR: {e}")
    print()
