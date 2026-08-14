# -*- coding: utf-8 -*-
"""
排查 AMBITIOUS MISSION アフターエピソード2：kungal 角色列表 vs 补齐条件。
流程：搜索游戏 → detail 角色列表 → 每个角色详情（简介/links/image）→ 输出表格。
用法: python probe_game_chars.py "游戏名"
"""
import json
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
            m = PAT.search(r.read().decode("utf-8", errors="replace"))
            return m.group(1).strip() if m else None
    except Exception:
        return None


def main():
    keyword = sys.argv[1] if len(sys.argv) > 1 else "AMBITIOUS MISSION"
    env = kget(f"/search?keywords={urllib.request.quote(keyword)}&type=galgame&page=1&limit=5")
    items = (env.get("data") or {}).get("items") or []
    print(f"搜索「{keyword}」命中 {len(items)} 条:")
    for it in items[:5]:
        nm = (it.get("name") or {})
        print(f"  gid={it.get('id')} zh-cn={nm.get('zh-cn')!r} ja-jp={nm.get('ja-jp')!r} en-us={nm.get('en-us')!r}")

    # 选第一个（或手动指定 gid）
    if not items:
        print("无搜索结果")
        return
    gid = int(sys.argv[2]) if len(sys.argv) > 2 else items[0]["id"]
    d = kget(f"/galgame/{gid}").get("data") or {}
    print(f"\n===== gid={gid} {d.get('name',{}).get('zh-cn') or d.get('name',{}).get('ja-jp')} =====")
    print(f"vndb_id={d.get('vndb_id')} characters={len(d.get('characters') or [])}")

    # 并发拉角色详情
    rows = []
    with ThreadPoolExecutor(max_workers=8) as ex:
        futs = {}
        for c in (d.get("characters") or []):
            futs[ex.submit(kget, f"/galgame-character/{c['id']}")] = c
        for fut in as_completed(futs):
            c = futs[fut]
            try:
                cd = (fut.result().get("data") or {})
            except Exception as e:
                rows.append((c.get("id"), c.get("name"), None, None, None, None, str(e)))
                continue
            intros = cd.get("intros") or []
            zh = next((it.get("intro") for it in intros
                       if (it.get("lang") or "").lower().startswith("zh")), None)
            links = cd.get("links") or []
            bgm_id = None
            for lk in links:
                if lk.get("source") == "bangumi" and lk.get("url"):
                    bgm_id = lk["url"].rstrip("/").split("/")[-1]
            rows.append((c.get("id"), c.get("name"), cd.get("image"), zh, bgm_id, links, None))
            time.sleep(0.05)

    rows.sort(key=lambda r: r[0] or 0)
    print(f"\n{'id':>8} {'名字':<20} {'图':<4} {'中简介':<5} {'bgm':<8} 链接")
    for rid, name, img, zh, bgm_id, links, err in rows:
        if err:
            print(f"{rid:>8} {name} FAIL {err}")
            continue
        linkstr = ",".join(f"{lk.get('source')}:{lk.get('url','').split('/')[-1]}" for lk in links)
        print(f"{rid:>8} {str(name)[:20]:<20} {'有' if img else '无':<4} "
              f"{'有' if zh else '无':<5} {str(bgm_id)[:8]:<8} {linkstr[:60]}")

    # bgm 页面简体中文名（对每个有 bgm id 的角色）
    print("\n===== bgm 简体中文名 =====")
    with ThreadPoolExecutor(max_workers=4) as ex:
        futs = {}
        for rid, name, img, zh, bgm_id, links, err in rows:
            if bgm_id and not err:
                futs[ex.submit(bgm_page, int(bgm_id))] = (rid, name)
        for fut in as_completed(futs):
            rid, name = futs[fut]
            print(f"  bgm={rid} {name} 简体中文名={fut.result()!r}")


if __name__ == "__main__":
    main()
