# -*- coding: utf-8 -*-
"""
抽样统计 kungal detail.characters 的 spoiler 字段分布（剧透角色占比）。
用法: python probe_spoiler.py --sample 12 --seed 3
"""
import json
import random
import sys
import urllib.request
from collections import Counter
from concurrent.futures import ThreadPoolExecutor, as_completed

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

K_BASE = "https://www.kungal.com/api"
K_COOKIE = 'KUNGalgameSettings={"showKUNGalgameContentLimit":"all"}'
K_UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"


def kget(path):
    req = urllib.request.Request(K_BASE + path, headers={"User-Agent": K_UA, "Cookie": K_COOKIE})
    with urllib.request.urlopen(req, timeout=15) as r:
        return json.loads(r.read().decode("utf-8"))


def main():
    args = sys.argv[1:]
    sample = 12
    seed = 3
    if "--sample" in args:
        sample = int(args[args.index("--sample") + 1])
    if "--seed" in args:
        seed = int(args[args.index("--seed") + 1])

    total = kget("/galgame?page=1&limit=1")["data"]["total"]
    random.seed(seed)
    gids = random.sample(range(1, total + 1), min(sample, total))

    dist = Counter()
    rows = []
    with ThreadPoolExecutor(max_workers=8) as ex:
        futs = {ex.submit(kget, f"/galgame/{g}") for g in gids}
        for fut in as_completed(futs):
            try:
                d = fut.result().get("data") or {}
            except Exception:
                continue
            chars = d.get("characters") or []
            nm = (d.get("name") or {}).get("zh-cn") or (d.get("name") or {}).get("ja-jp") or d.get("id")
            spoilers = [c for c in chars if (c.get("spoiler") or 0) > 0]
            for c in chars:
                dist[c.get("spoiler", "缺失")] += 1
            if spoilers:
                rows.append((d.get("id"), nm, len(chars),
                             [(c.get("name"), c.get("spoiler")) for c in spoilers]))
            else:
                rows.append((d.get("id"), nm, len(chars), []))

    print("spoiler 值分布:", dict(dist))
    print(f"\n抽样 {len(rows)} 游戏，有剧透角色的游戏: {sum(1 for r in rows if r[3])}")
    for gid, nm, n, sp in rows:
        if sp:
            print(f"  gid={gid} {nm} chars={n} 剧透: {sp}")
        else:
            print(f"  gid={gid} {nm} chars={n} (无剧透)")


if __name__ == "__main__":
    main()
