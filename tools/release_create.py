"""创建 GitHub Release v1.2.0 并上传 plugin.pvnplugin.zip（token 从 git 凭据管理器提取，不落盘）"""
import json
import subprocess
import urllib.request
import urllib.error

REPO = "Hansnmor/PotatoVN.Plugins.LibraryPlus"
ZIP = r"E:\_Code\_hansnmor\_WORKSPACE\zcode\PotatoVN.Plugins.LibraryPlus\PotatoVN.App.PluginBase\artifacts\plugin.pvnplugin.zip"

# 从 git 凭据管理器提取 token
out = subprocess.run(["git", "credential", "fill"], input="protocol=https\nhost=github.com\n",
                     capture_output=True, text=True).stdout
token = next((l.split("=", 1)[1] for l in out.splitlines() if l.startswith("password=")), None)
if not token:
    raise SystemExit("无法获取 GitHub 凭据")

def api(url, data=None, method="POST", content_type="application/json", raw=None):
    body = raw if raw is not None else (json.dumps(data).encode() if data else None)
    req = urllib.request.Request(url, data=body, method=method)
    req.add_header("Authorization", "Bearer " + token)
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("Content-Type", content_type)
    try:
        with urllib.request.urlopen(req) as r:
            return json.loads(r.read())
    except urllib.error.HTTPError as e:
        print("API 错误:", e.code, e.read().decode()[:600])
        raise

body = """## v1.2.0：kungal 数据源 + 批量搜刮 + 双轴分类

### 新增功能
- **kungal 数据源**：原生源选择器可选 Kungal，单游戏可手动搜刮中文简介与标签（三层匹配：gid 记忆 → VNDB ID → 名称搜索）
- **批量搜刮**：扩展库页「更多搜刮」对筛选/勾选的游戏批量拉取 kungal + Bangumi 数据（简介规则、标签合并、全局进度状态、完成自动刷新）
- **双轴分类**：内容轴（萌作/剧情作/拔作/其他）+ 形态轴（传统ADV/非传统ADV），社区投票 + 标签热度 + Bangumi 投票信号，统计与筛选双轴联动
- **手动覆盖**：右键游戏可手动指定分类/形态（多选勾选时批量应用），持久化优先于自动分类
- **功能说明**：扩展库页「功能说明」按钮查看完整介绍

### 使用提示
- 建议 kungal 搜刮放在混合搜刮之后（混合搜刮会覆盖标签）
- 未搜刮 kungal 的游戏使用基础分类（VNDB/Bangumi 标签规则）
- Bangumi tag 投票数据需在 PotatoVN 中登录 Bangumi 账号后采集"""

rel = api(f"https://api.github.com/repos/{REPO}/releases",
          {"tag_name": "v1.2.0", "name": "v1.2.0", "body": body})
print("Release 已创建:", rel["html_url"])

with open(ZIP, "rb") as f:
    asset = api(f"https://uploads.github.com/repos/{REPO}/releases/{rel['id']}/assets?name=plugin.pvnplugin.zip",
                content_type="application/zip", raw=f.read())
print("插件 zip 已上传:", asset["browser_download_url"])
