"""Enter Blue Dawn and verify it is really running, without guessing coordinates.

Campaign cards are custom-drawn: they expose unlabeled Buttons whose `Tag` is
the campaign file path, so the engine can identify and click them by index
(DUMP_FORM_BUTTONS / CLICK_FORM_BUTTON). Every step waits for the expected form
and fails loudly instead of clicking a stale layout.
"""
from __future__ import annotations

import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import live_harness as H
from verify_regressions import _read_simulation_time

CAMPAIGN = "Chains Of War"


def _forms() -> list[dict]:
    response = H.send("DUMP_OPEN_FORMS", timeout=30)
    if response.get("status") != "ok" or not isinstance(response.get("forms"), list):
        raise RuntimeError("form observation failed: " + str(response))
    return response["forms"]


def _names() -> list[str]:
    return [form.get("name", "") for form in _forms()]


def _title() -> str:
    titles = [form.get("text", "") for form in _forms() if form.get("name") == "MainForm"]
    if len(titles) != 1:
        raise RuntimeError("one MainForm must be observed")
    return titles[0]


def _wait_for(name: str, timeout: float = 60.0) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        if name in _names():
            return True
        time.sleep(2)
    return False


def _click_form_button(form: str, tag_part: str) -> int:
    """Click the first button whose Tag contains tag_part; returns its index."""
    dump = H.send("DUMP_FORM_BUTTONS " + form, timeout=30)
    if dump.get("status") != "ok":
        raise RuntimeError("button dump failed: " + str(dump))
    for button in dump.get("buttons") or []:
        if tag_part.casefold() in (button.get("tag") or "").casefold():
            result = H.send(f"CLICK_FORM_BUTTON {form} {button['i']}", timeout=30)
            if result.get("status") != "ok":
                raise RuntimeError("button click failed: " + str(result))
            return int(button["i"])
    raise RuntimeError(f"no button in {form} has tag {tag_part!r}")


def enter_campaign(first_scenario: bool = True) -> None:
    if "CampaignPlayWindow" not in _names():
        result = H.send("CLICK_WPF Button_Campaign", timeout=30)
        if result.get("clicked", 0) < 1:
            raise RuntimeError("campaign button was not clickable: " + str(result))
        if not _wait_for("CampaignPlayWindow"):
            raise RuntimeError("campaign list did not open")
    time.sleep(1.0)
    _click_form_button("CampaignPlayWindow", CAMPAIGN)
    time.sleep(2.0)
    result = H.send("CLICK_BUTTON 开始/启动新建战役", timeout=30)
    if result.get("status") != "ok":
        raise RuntimeError("could not start the campaign: " + str(result))
    if not _wait_for("CampaignScenarioWindow", 90):
        raise RuntimeError("campaign scenario list did not open")
    time.sleep(3.0)
    result = H.send("CLICK_BUTTON 开始想定", timeout=30)
    if result.get("status") != "ok":
        raise RuntimeError("could not open the first scenario: " + str(result))
    if not _wait_for("ChooseSide", 120):
        raise RuntimeError("side selection did not open")
    time.sleep(2.0)
    result = H.send("CLICK_BUTTON 进入想定", timeout=30)
    if result.get("status") != "ok":
        raise RuntimeError("could not enter the scenario: " + str(result))
    deadline = time.time() + 150
    while time.time() < deadline and "Blue Dawn" not in _title():
        time.sleep(3)


def ensure_running() -> tuple[int, int]:
    """Verify Blue Dawn identity and that the scenario clock advances."""
    title = _title()
    if "blue dawn" not in title.casefold():
        raise RuntimeError("Blue Dawn is not active: " + title)
    before = _read_simulation_time(H)
    if before is None:
        raise RuntimeError("simulation time is unavailable")
    time.sleep(3)
    after = _read_simulation_time(H)
    if after is not None and after > before:
        return before, after
    H.send("CLICK_TOOLBAR TSB_StartGame", timeout=30)
    time.sleep(4)
    before = _read_simulation_time(H)
    time.sleep(4)
    after = _read_simulation_time(H)
    if before is None or after is None or after <= before:
        raise RuntimeError("scenario clock does not advance; the game is paused or blocked")
    return before, after


def main() -> int:
    if not H.game_pid():
        print("FAIL: game is not running")
        return 2
    try:
        if "blue dawn" not in _title().casefold():
            enter_campaign()
        before, after = ensure_running()
        if "blue dawn" not in _title().casefold():
            raise RuntimeError("scenario changed during entry verification")
        print(f"PASS: {_title()}; simulation {before} → {after}")
        return 0
    except (RuntimeError, OSError) as error:
        print("FAIL: " + str(error))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
