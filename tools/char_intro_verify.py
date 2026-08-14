# -*- coding: utf-8 -*-
"""
kungal 角色简介中文覆盖率正式验证（并发版）
流程：并发拉游戏 detail → 收集角色 id → 并发拉角色详情 /api/galgame-character/:id
统计：有简介/中文(machine标记)/日文/英文/无简介；抽查机器翻译质量样本。
用法: python char_intro_verify.py --sample 40 --seed 20260814 [--max-chars 8]
"""
import json
import random
import sys
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

BASE = "https://www.kungal.com/api"
COOKIE = 'KUNGalgameSettings={"showKUNGalgameContentLimit":"all"}'
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
DELAY = 0.1


def get(path):
    req = urllib.request.Request(BASE + path, headers={"User-Agent": UA, "Cookie": COOKIE})
    with urllib.request.urlopen(req, timeout=15) as r:
        return json.loads(r.read().decode("utf-8"))


def is_chinese(text):
    """与 KungalPhraser.IsChinese 一致：含汉字且无假名（排除・/ー）"""
    if not text or not text.strip():
        return False
    has_han = False
    for ch in text:
        cp = ord(ch)
        if 0x4E00 <= cp <= 0x9FFF:
            has_han = True
        if 0x3040 <= cp <= 0x30FF and ch not in ("\u30FB", "\u30FC"):
            return False
    return has_han


def is_japanese(text):
    if not text or not text.strip():
        return False
    for ch in text:
        cp = ord(ch)
        if 0x3040 <= cp <= 0x30FF and ch not in ("\u30FB", "\u30FC"):
            return True
    return False


def pick_zh(intros):
    """intros 数组里挑中文（优先简体 zh-Hans/zh-CN），返回 (文本, machine, lang) 或 None"""
    if not intros:
        return None
    pref = None
    for it in intros:
        lang = (it.get("lang") or "").lower()
        intro = it.get("intro") or ""
        if not intro.strip():
            continue
        if lang.startswith("zh-hans") or lang == "zh-cn":
            return intro, bool(it.get("machine")), lang
        if lang.startswith("zh") and pref is None:
            pref = (intro, bool(it.get("machine")), lang)
    return pref


def fetch_game(gid):
    """拉游戏详情，返回 (gid, nm, char_ids) 或 None"""
    try:
        time.sleep(DELAY)
        d = get(f"/galgame/{gid}").get("data") or {}
        chars = d.get("characters") or []
        nm = (d.get("name") or {}).get("zh-cn") or (d.get("name") or {}).get("ja-jp") or gid
        char_ids = [c["id"] for c in chars if isinstance(c.get("id"), int)]
        return gid, nm, char_ids
    except Exception as e:
        print(f"  [gid={gid}] detail FAIL: {e}", flush=True)
        return None


def fetch_char(cid):
    """拉角色详情，返回 (cid, cd) 或 (cid, None)"""
    try:
        time.sleep(DELAY)
        cd = get(f"/galgame-character/{cid}").get("data") or {}
        return cid, cd
    except Exception:
        return cid, None


def main():
    args = sys.argv[1:]
    sample = 40
    seed = 20260814
    max_chars = 8
    if "--sample" in args:
        sample = int(args[args.index("--sample") + 1])
    if "--seed" in args:
        seed = int(args[args.index("--seed") + 1])
    if "--max-chars" in args:
        max_chars = int(args[args.index("--max-chars") + 1])

    env = get("/galgame?page=1&limit=1")
    total = env["data"]["total"]
    print(f"kungal 条目总量: {total}, 抽样 {sample}, seed={seed}, max_chars/游戏={max_chars}", flush=True)

    random.seed(seed)
    gids = random.sample(range(1, total + 1), min(sample, total))

    # 阶段1：并发拉游戏详情
    games = []
    with ThreadPoolExecutor(max_workers=8) as ex:
        futs = {ex.submit(fetch_game, gid): gid for gid in gids}
        for fut in as_completed(futs):
            r = fut.result()
            if r:
                games.append(r)
    games.sort(key=lambda r: r[0])
    print(f"游戏详情 OK: {len(games)}/{len(gids)}", flush=True)

    # 收集角色 id
    char_ids = []
    for gid, nm, ids in games:
        if ids and max_chars:
            ids = ids[:max_chars]
        char_ids.extend(ids)
    print(f"角色总数: {len(char_ids)}", flush=True)

    # 阶段2：并发拉角色详情
    char_map = {}
    with ThreadPoolExecutor(max_workers=12) as ex:
        futs = {ex.submit(fetch_char, cid): cid for cid in char_ids}
        for fut in as_completed(futs):
            cid, cd = fut.result()
            char_map[cid] = cd

    # 阶段3：统计
    stats = {
        "games": len(games),
        "games_with_chars": 0,
        "chars_queried": len(char_ids),
        "char_request_ok": sum(1 for cd in char_map.values() if cd),
        "has_intro": 0,
        "zh_intro": 0,
        "zh_machine": 0,
        "ja_intro": 0,
        "en_intro": 0,
        "no_intro": 0,
        "request_fail": 0,
    }
    games_no_chars = []
    games_no_zh = []
    zh_samples = []
    detail_rows = []

    for gid, nm, ids in games:
        if not ids:
            games_no_chars.append((gid, nm))
            detail_rows.append((gid, nm, 0, 0, 0))
            continue
        stats["games_with_chars"] += 1
        queried = ids[:max_chars] if max_chars else ids
        zh_count = 0
        for cid in queried:
            cd = char_map.get(cid)
            if not cd:
                stats["request_fail"] += 1
                continue
            intros = cd.get("intros") or []
            intro = cd.get("intro") or ""
            zh = pick_zh(intros)
            if zh:
                text, machine, lang = zh
                stats["has_intro"] += 1
                stats["zh_intro"] += 1
                if machine:
                    stats["zh_machine"] += 1
                if len(zh_samples) < 8 and machine:
                    zh_samples.append((nm, cd.get("name"), text[:120]))
                zh_count += 1
            elif intro.strip() and is_chinese(intro):
                stats["has_intro"] += 1
                stats["zh_intro"] += 1
                zh_count += 1
            elif intro.strip() and is_japanese(intro):
                stats["has_intro"] += 1
                stats["ja_intro"] += 1
            elif intro.strip():
                stats["has_intro"] += 1
                stats["en_intro"] += 1
            else:
                stats["no_intro"] += 1
        if zh_count == 0:
            games_no_zh.append((gid, nm, len(ids)))
        detail_rows.append((gid, nm, len(ids), len(queried), zh_count))

    print("\n===== 统计 =====")
    print(json.dumps(stats, ensure_ascii=False, indent=2))
    print(f"\n无 characters 的游戏({len(games_no_chars)}):")
    for gid, nm in games_no_chars:
        print(f"  gid={gid} {nm}")
    print(f"\n有角色但全无中文简介的游戏({len(games_no_zh)}):")
    for gid, nm, n in games_no_zh:
        print(f"  gid={gid} chars={n} {nm}")
    print("\n明细 (gid, 名称, 角色总数, 查询数, 中文简介角色数):")
    for gid, nm, n, q, zh in detail_rows:
        print(f"  gid={gid} chars={n} queried={q} zh={zh} | {nm}")
    if stats["chars_queried"]:
        ok = stats["char_request_ok"]
        print(f"\n[结论] 角色详情请求成功率: {ok}/{stats['chars_queried']} = {ok/stats['chars_queried']*100:.1f}%")
        print(f"[结论] 中文简介覆盖率: {stats['zh_intro']}/{ok} = {stats['zh_intro']/ok*100:.1f}%")
        if stats["zh_intro"]:
            print(f"[结论] 其中机器翻译: {stats['zh_machine']}/{stats['zh_intro']} = {stats['zh_machine']/stats['zh_intro']*100:.1f}%")
        print(f"[结论] 日文简介(无中文): {stats['ja_intro']}, 无简介: {stats['no_intro']}, 请求失败: {stats['request_fail']}")
    if zh_samples:
        print("\n===== 机器翻译质量抽查 =====")
        for gnm, cnm, text in zh_samples:
            print(f"  [{gnm} / {cnm}] {text}")


if __name__ == "__main__":
    main()
