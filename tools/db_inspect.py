"""解析 pvn_data.db 的插件数据，检查目标游戏的 KungalData/BgmData 与分类判定路径"""
import json

BS = chr(92)  # 反斜杠

data = open(r'C:/Users/qq192/AppData/Local/Packages/37126GoldenPotato137.PotatoVN_8vtbc0gbd4jey/LocalState/pvn_data.db', 'rb').read()


def extract(key):
    idx = data.find(('"%s":' % key).encode())
    if idx < 0:
        return None
    seg = data[idx:idx + 2000000].decode('utf-8', errors='replace')
    start = seg.find('{')
    depth = 0
    in_str = False
    esc = False
    for i in range(start, len(seg)):
        c = seg[i]
        if in_str:
            if esc:
                esc = False
            elif c == BS:
                esc = True
            elif c == '"':
                in_str = False
        else:
            if c == '"':
                in_str = True
            elif c == '{':
                depth += 1
            elif c == '}':
                depth -= 1
                if depth == 0:
                    return json.loads(seg[start:i + 1])
    return None


kungal = extract('KungalData')
bgm = extract('BgmData')
print('KungalData:', len(kungal) if kungal else 'None', '条 | BgmData:', len(bgm) if bgm else 'None', '条')

targets = {131: '恋爱成双结爱', 244: '青空下的加缪', 130: '恋爱成双', 2007: '茂伸奇谈HE', 33: 'NUKITASHI'}
for g, name in targets.items():
    hit = [(u, k) for u, k in kungal.items() if k.get('Gid') == g]
    if hit:
        u, kd = hit[0]
        basaku = [t.get('Name') for t in kd.get('Tags', []) if '拔作' in t.get('Name', '')]
        has_bgm = u in bgm
        bgm_basaku = [t.get('Name') for t in bgm[u]] if has_bgm else []
        print(f'gid={g} {name}: 投票={kd.get("TypeVotes")} kungal拔作tag={basaku} '
              f'BgmData={"有" if has_bgm else "无"} bgm拔作tag={[t for t in bgm_basaku if "拔作" in t]}')
    else:
        print(f'gid={g} {name}: 无 KungalData')
