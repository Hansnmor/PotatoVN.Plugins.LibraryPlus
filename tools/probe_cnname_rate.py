# -*- coding: utf-8 -*-
"""
实测覆盖率：日文名角色中，bgm 页面有「简体中文名」的比例。
流程：随机 gid → kungal detail 角色 → kungal 角色详情拿 links.bangumi id → bgm 页面解析「简体中文名」。
用法: python probe_cnname_rate.py --sample 8 --seed 1
"""
import json
import random
import re
import sys
import time
import urllib.request
import urllib.error
from concurrent.futures import ThreadPoolExecutor, as_completed

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

K_BASE = "https://www.kungal.com/api"
K_COOKIE = 'KUNGalgameSettings={"showKUNGalgameContentLimit":"all"}'
K_UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0"
B_UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PotatoVN.LibraryPlus/1.0"
PAT = re.compile(r'<span class="tip">简体中文名: </span>([^<]+)')


def kget(path):
    req = urllib.request.Request(K_BASE + path, headers={"User-Agent": K_UA, "Cookie": K_COOKIE})
    with urllib.request.urlopen(req, timeout=15) as r:
        return json.loads(r.read().decode("utf-8"))


def bgm_page(cid):
    req = urllib.request.Request(f"https://bgm.tv/character/{cid}", headers={"User-Agent": B_UA})
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            html = r.read().decode("utf-8", errors="replace")
        m = PAT.search(html)
        return m.group(1).strip() if m else None
    except Exception:
        return None


def is_japanese_name(text):
    if not text:
        return False
    for ch in text:
        cp = ord(ch)
        if 0x3040 <= cp <= 0x30FF and ch not in ("\u30FB", "\u30FC"):
            return True
    return False


def main():
    args = sys.argv[1:]
    sample = 8
    seed = 1
    if "--sample" in args:
        sample = int(args[args.index("--sample") + 1])
    if "--seed" in args:
        seed = int(args[args.index("--seed") + 1])

    total = kget("/galgame?page=1&limit=1")["data"]["total"]
    random.seed(seed)
    gids = random.sample(range(1, total + 1), min(sample, total))

    # 并发拉游戏详情 → 收集角色
    games = []
    with ThreadPoolExecutor(max_workers=6) as ex:
        futs = {ex.submit(kget, f"/galgame/{g}") for g in gids}
        for fut in as_completed(futs):
            try:
                d = fut.result().get("data") or {}
                games.append(d)
            except Exception:
                pass
    print(f"游戏详情 OK: {len(games)}")

    # 并发拉 kungal 角色详情，收集 bgm id
    char_rows = []  # (kungal_name, bgm_id)
    with ThreadPoolExecutor(max_workers=8) as ex:
        futs = []
        for d in games:
            for c in (d.get("characters") or [])[:6]:
                futs.append((c["id"], c.get("name"), ex.submit(kget, f"/galgame-character/{c['id']}")))
        for cid, cname, fut in futs:
            try:
                cd = fut.result().get("data") or {}
            except Exception:
                continue
            bgm_id = None
            for link in cd.get("links") or []:
                if link.get("source") == "bangumi" and link.get("url"):
                    bgm_id = link["url"].rstrip("/").split("/")[-1]
                    break
            char_rows.append((cname, bgm_id))
    rows = [r for r in char_rows if r[1] and r[1].isdigit()]
    print(f"角色总数 {len(char_rows)}，有 bgm id {len(rows)}")

    # 并发拉 bgm 页面
    results = []
    with ThreadPoolExecutor(max_workers=4) as ex:
        futs = {ex.submit(bgm_page, int(bid)): (nm, bid) for nm, bid in rows}
        for fut in as_completed(futs):
            nm, bid = futs[fut]
            results.append((nm, bid, fut.result()))
            time.sleep(0.1)

    jp = [r for r in results if is_japanese_name(r[0])]
    jp_with_cn = [r for r in jp if r[2]]
    print(f"\n===== 覆盖率 =====")
    print(f"全部角色: {len(results)}，有简体中文名: {sum(1 for r in results if r[2])} ({sum(1 for r in results if r[2])/max(len(results),1)*100:.0f}%)")
    print(f"日文名角色: {len(jp)}，有简体中文名: {len(jp_with_cn)} ({len(jp_with_cn)/max(len(jp),1)*100:.0f}%)")
    print(f"中文名角色(无标签预期): {len(results)-len(jp)}")
    print("\n===== 明细（前 30）=====")
    for nm, bid, cn in results[:30]:
        print(f"  bgm={bid} jp={'日' if is_japanese_name(nm) else '中'} cn={cn!r} | {nm}")


if __name__ == "__main__":
    main()
