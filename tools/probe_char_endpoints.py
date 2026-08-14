# -*- coding: utf-8 -*-
"""
探测 kungal 角色简介的来源端点。
已知：/api/galgame/:gid 的 characters 只有 id/name/kind/spoiler/image/figure/voices（无简介）。
候选：独立角色资源（类比 galgame-tag/galgame-rating 命名风格）、子资源、翻译参数。
用法: python probe_char_endpoints.py
"""
import json
import sys
import urllib.request
import urllib.error

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

BASE = "https://www.kungal.com/api"
COOKIE = 'KUNGalgameSettings={"showKUNGalgameContentLimit":"all"}'
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")

# (说明, 路径) —— gid=5994, 角色 id=170426
CANDIDATES = [
    ("独立角色资源(单数)", "/galgame-character/170426"),
    ("独立角色资源(复数)", "/galgame-characters/170426"),
    ("character 单数", "/character/170426"),
    ("characters 单数", "/characters/170426"),
    ("galgame 子资源 characters", "/galgame/5994/characters"),
    ("galgame 子资源 character", "/galgame/5994/character"),
    ("角色搜索 by id", "/galgame-character/search/170426"),
    ("galgame-character 列表", "/galgame-character?page=1&limit=5"),
    ("带语言参数 detail", "/galgame/5994?lang=zh-cn"),
    ("带翻译参数 detail", "/galgame/5994?translate=1"),
]


def get_raw(path):
    req = urllib.request.Request(BASE + path, headers={"User-Agent": UA, "Cookie": COOKIE})
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            body = r.read().decode("utf-8", errors="replace")
            return r.status, body
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", errors="replace")[:200]
    except Exception as e:
        return -1, str(e)


for desc, path in CANDIDATES:
    status, body = get_raw(path)
    snippet = body[:300].replace("\n", " ")
    print(f"[{status}] {desc}: {path}")
    print(f"    {snippet}")
    print()
