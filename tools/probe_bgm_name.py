# -*- coding: utf-8 -*-
"""
找 Bangumi「简体中文名」真实来源：
1) v0 搜索接口 /v0/search/characters 字段
2) 抓角色页 HTML 找「简体中文名」标签
用法: python probe_bgm_name.py [char_id]
"""
import json
import re
import sys
import urllib.request
import urllib.error

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

UA = "PotatoVN.LibraryPlus/1.0 (plugin)"

def get_json(url):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            return r.status, json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", errors="replace")[:150]
    except Exception as e:
        return -1, str(e)

print("=== 1) v0 搜索角色（古河渚）===")
status, d = get_json("https://api.bgm.tv/v0/search/characters?keyword=%E5%8F%A4%E6%B2%B3%E6%B8%9A&limit=5")
if status == 200:
    for it in (d.get("data") or [])[:5]:
        print(json.dumps(it, ensure_ascii=False)[:300])
else:
    print(f"FAIL {status}: {str(d)[:120]}")

print("\n=== 2) 角色页 HTML 搜「简体中文名」（id=84 草薙素子）===")
cid = sys.argv[1] if len(sys.argv) > 1 else "84"
try:
    req = urllib.request.Request(
        f"https://bgm.tv/character/{cid}",
        headers={"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"},
    )
    html = urllib.request.urlopen(req, timeout=15).read().decode("utf-8", errors="replace")
    print("页面长度:", len(html))
    for m in re.finditer(r".{50}简体中文名.{150}", html, re.S):
        print("---", m.group(0)[:200].replace("\n", " "))
    m = re.search(r"<h1[^>]*>(.*?)</h1>", html, re.S)
    if m:
        print("h1:", re.sub(r"<[^>]+>", "", m.group(1)).strip())
except Exception as e:
    print("页面抓取失败:", e)
