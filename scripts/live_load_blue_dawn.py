"""一键进入战争链战役第一关 Blue Dawn（蓝色拂晓）并开始推演。

为什么需要它：Blue Dawn 是**战役关卡**（CampaignID 非空），不在「加载想定」列表里，
只能走 战役 → 系列战役 → Chains Of War → 开始新建战役 → 开始想定 → 选阵营 这条路。
每一步都要等窗口真正出现再点，且必须按真实坐标点（卡片列表是自绘的，没有可点击控件名）。
"""

import sys
import time

sys.path.insert(0, 'scripts')
import live_harness as H


def forms() -> list[str]:
    d = H.send('DUMP_OPEN_FORMS', timeout=60)
    return [f.get('name') or '' for f in d.get('forms') or []]


def wait_form(name: str, timeout: float = 60.0) -> bool:
    t0 = time.time()
    while time.time() - t0 < timeout:
        if name in forms():
            return True
        time.sleep(1.5)
    return False


def big_win(min_w=800) -> dict | None:
    for w in H.list_windows(H.game_pid()):
        if w['visible'] and w['rect']['Width'] >= min_w and w['rect']['X'] > -1000:
            return w
    return None


def click_until_form(x: int, y: int, form: str, tries: int = 5,
                     gap: float = 3.0, focus_hwnd: int | None = None) -> bool:
    """点一下并等目标窗体出现；没出现就重点。

    踩过的坑：启动器/战役列表都是自绘卡片，没有可点击控件名，只能按坐标点；
    而窗口刚弹出时命中率不高（首点常常落在还没就绪的控件上）。
    单次点击 + 短超时会假失败，必须「点→等→再点」。
    """
    for i in range(tries):
        if focus_hwnd:
            H.focus(focus_hwnd)
            time.sleep(0.5)
        H.click(x, y)
        if wait_form(form, gap * 2):
            return True
    return False


def main() -> int:
    pid = H.game_pid()
    if not pid:
        print('游戏未运行')
        return 2

    # ① 战役 → 系列战役
    H.send('CLICK_MENU TSMI_Campaign', timeout=30)
    launcher = None
    for _ in range(10):
        time.sleep(2)
        launcher = big_win(800)
        if launcher:
            break
    if not launcher:
        print('启动器没出来')
        return 1
    r = launcher['rect']
    if not click_until_form(r['X'] + 736, r['Y'] + 151, 'CampaignPlayWindow',
                            tries=5, focus_hwnd=launcher['hwnd']):
        print('战役列表没出来')
        return 1

    camp = next((w for w in H.list_windows(pid)
                 if w['visible'] and w['rect']['Width'] == 1030), None)
    if not camp:
        print('找不到战役列表窗口')
        return 1
    cr = camp['rect']
    # Chains Of War 卡片 → 开始/启动新建战役。两步都可能首点落空，各自重试。
    H.focus(camp['hwnd'])
    time.sleep(0.8)
    H.click(cr['X'] + 150, cr['Y'] + 225)
    time.sleep(2.5)
    if not click_until_form(cr['X'] + 441, cr['Y'] + 582, 'CampaignScenarioWindow',
                            tries=5, focus_hwnd=camp['hwnd']):
        print('战役想定窗口没出来')
        return 1

    scen = next((w for w in H.list_windows(pid)
                 if w['visible'] and w['rect']['Width'] == 1001), None)
    if not scen:
        print('找不到战役想定窗口')
        return 1
    sr = scen['rect']
    if not click_until_form(sr['X'] + 141, sr['Y'] + 630, 'ChooseSide',
                            tries=6, gap=4.0, focus_hwnd=scen['hwnd']):
        print('阵营选择窗口没出来')
        return 1

    # ② 阵营选择 → 进入想定
    H.send('CLICK_BUTTON 进入想定', timeout=25)
    time.sleep(15)

    d = H.send('DUMP_OPEN_FORMS', timeout=60)
    title = next((f.get('text') or '' for f in d.get('forms') or []
                  if f.get('name') == 'MainForm'), '')
    print('MainForm:', title)

    # ③ 开始推演
    H.send('CLICK_TOOLBAR TSB_StartGame', timeout=25)
    time.sleep(10)
    ok = 'Blue Dawn' in title
    print('RESULT:', 'OK' if ok else 'FAILED')
    return 0 if ok else 1


if __name__ == '__main__':
    raise SystemExit(main())
