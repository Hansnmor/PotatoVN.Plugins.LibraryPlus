"""M0 匹配正确率抽样验证（kungal）
模拟插件匹配流程：列表取样本 → vndb_id 搜索验证 → 中文名搜索验证
用法: python m0_match_verify.py
"""
import json
import time
import urllib.parse
import urllib.request

BASE = "https://www.kungal.com/api"
UA = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36",
      "Cookie": 'KUNGalgameSettings={"showKUNGalgameContentLimit":"all"}'}
SAMPLE = 50
DELAY = 0.3


def get(path):
    req = urllib.request.Request(BASE + path, headers=UA)
    with urllib.request.urlopen(req, timeout=15) as r:
        return json.loads(r.read().decode("utf-8"))


def main():
    # 1. 取样本
    data = get(f"/galgame?page=1&limit={SAMPLE}")["data"]
    cards = data["galgames"]
    print(f"样本: {len(cards)} 个（total={data['total']}）")
    time.sleep(DELAY)

    # 2. 逐样本: detail 拿 vndb_id + 中文名
    samples = []
    for c in cards:
        gid = c["id"]
        try:
            d = get(f"/galgame/{gid}")["data"]
            samples.append({
                "gid": gid,
                "vndb_id": d.get("vndb_id"),
                "name_zh": (d["name"].get("zh-cn") or "").strip(),
                "name_ja": (d["name"].get("ja-jp") or "").strip(),
            })
        except Exception as e:
            print(f"  [warn] gid={gid} detail 失败: {e}")
        time.sleep(DELAY)

    # 3. vndb_id 搜索验证
    hit_vndb_first = hit_vndb_any = no_vndb = 0
    vndb_fails = []
    for s in samples:
        if not s["vndb_id"]:
            no_vndb += 1
            continue
        try:
            items = get(f"/search?keywords={s['vndb_id']}&type=galgame&page=1&limit=5")["data"]["items"]
        except Exception:
            continue
        gids = [i["id"] for i in items]
        if gids and gids[0] == s["gid"]:
            hit_vndb_first += 1
        if s["gid"] in gids:
            hit_vndb_any += 1
        else:
            vndb_fails.append((s["gid"], s["vndb_id"], gids))
        time.sleep(DELAY)

    # 4. 中文名搜索验证
    hit_name_first = hit_name_any = name_empty = 0
    name_fails = []
    for s in samples:
        if not s["name_zh"]:
            name_empty += 1
            continue
        try:
            kw = urllib.parse.quote(s["name_zh"])
            items = get(f"/search?keywords={kw}&type=galgame&page=1&limit=5")["data"]["items"]
        except Exception:
            continue
        gids = [i["id"] for i in items]
        if gids and gids[0] == s["gid"]:
            hit_name_first += 1
        if s["gid"] in gids:
            hit_name_any += 1
        else:
            name_fails.append((s["gid"], s["name_zh"], gids))
        time.sleep(DELAY)

    # 5. 汇总
    n_v = len(samples) - no_vndb
    n_n = len(samples) - name_empty
    print("\n===== 结果 =====")
    print(f"有 vndb_id 的样本: {n_v}/{len(samples)}（无 vndb_id: {no_vndb}）")
    print(f"vndb_id 搜索 首位命中: {hit_vndb_first}/{n_v} ({hit_vndb_first/max(n_v,1)*100:.0f}%)")
    print(f"vndb_id 搜索 含命中:   {hit_vndb_any}/{n_v} ({hit_vndb_any/max(n_v,1)*100:.0f}%)")
    print(f"中文名搜索 首位命中: {hit_name_first}/{n_n} ({hit_name_first/max(n_n,1)*100:.0f}%)")
    print(f"中文名搜索 含命中:   {hit_name_any}/{n_n} ({hit_name_any/max(n_n,1)*100:.0f}%)")
    if vndb_fails:
        print("\nvndb_id 搜索未命中样本:")
        for gid, vid, gids in vndb_fails[:8]:
            print(f"  gid={gid} vndb_id={vid} → 搜到 {gids[:3]}")
    if name_fails:
        print("\n名称搜索未命中的样本:")
        for gid, name, gids in name_fails[:8]:
            print(f"  gid={gid} 名={name} → 搜到 {gids[:3]}")


if __name__ == "__main__":
    main()
