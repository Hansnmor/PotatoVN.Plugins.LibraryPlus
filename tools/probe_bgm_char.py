# -*- coding: utf-8 -*-
"""
实测 Bangumi /v0/characters/{id}：字段结构 + name_cn 覆盖率抽查。
用法: python probe_bgm_char.py [id1 id2 ...]（缺省用 kungal 验证时见过的角色 id）
"""
import json
import sys
import urllib.request

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

UA = "PotatoVN.LibraryPlus/1.0 (plugin)"
BASE = "https://api.bgm.tv"


def get(path):
    req = urllib.request.Request(BASE + path, headers={"User-Agent": UA})
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            return r.status, json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", errors="replace")[:200]
    except Exception as e:
        return -1, str(e)


ids = [int(a) for a in sys.argv[1:]] if len(sys.argv) > 1 else [211740, 2543, 5275, 42712]

for cid in ids:
    status, d = get(f"/v0/characters/{cid}")
    if status != 200:
        print(f"[{status}] id={cid} FAIL: {str(d)[:120]}")
        continue
    name = d.get("name")
    name_cn = d.get("name_cn")
    keys = sorted(d.keys())
    print(f"[200] id={cid} name={name!r} name_cn={name_cn!r}")
    print(f"      keys={keys}")
    print()
