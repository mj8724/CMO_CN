"""观测「想定特性与拟真度选项」僵尸窗口是否在持续增长。

为什么要单独测：perf_probe_large 在 Blue Dawn 上判 FAIL，原因是同标题 WPF 窗口
累积到 4 个。这有两种可能，处置完全不同：
  A) 持续泄漏（数量随时间线性增长）→ 必须修，否则玩越久越卡；
  B) 一次性残留（数量稳定，是当初反复加载想定留下的）→ 只需重启游戏，不是缺陷。
区分办法：让推演跑一段时间，每分钟采样窗口数看趋势。
"""

import sys
import time
from collections import Counter

sys.path.insert(0, 'scripts')
import live_harness as H


def sample() -> dict:
    d = H.send('WPF_DUMP', timeout=300)
    titles = Counter()
    nodes = 0
    for w in d.get('windows') or []:
        t = (w.get('title') or '').strip()
        titles[t] += 1
        nodes += len(w.get('nodes') or [])
    zombies = {t: n for t, n in titles.items() if n > 1 and t and not t.startswith('ElementHost:')}
    return {
        'windows': len(d.get('windows') or []),
        'nodes': nodes,
        'zombies': zombies,
        'zombie_extra': sum(n - 1 for n in zombies.values()),
        'titles': dict(titles),
    }


def main() -> int:
    print('开始推演，观察 6 分钟（每 60 秒采样一次）…')
    H.send('TSB_StartGame', timeout=30)
    series = []
    for i in range(7):
        s = sample()
        series.append(s)
        z = s['zombie_extra']
        zt = ','.join(f'{n}x{t[:16]}' for t, n in s['zombies'].items()) or '无'
        print(f'  t={i * 60:>3}s  窗口={s["windows"]}  节点={s["nodes"]}  僵尸额外={z}  [{zt}]')
        if i < 6:
            time.sleep(60)
    counts = [s['zombie_extra'] for s in series]
    print()
    if counts[-1] > counts[0]:
        print(f'FAIL: 僵尸窗口在持续增长 {counts} ⇒ 真实泄漏，必须修')
        return 1
    print(f'PASS: 僵尸窗口数量稳定 {counts} ⇒ 一次性残留，重启游戏即可，非持续缺陷')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
