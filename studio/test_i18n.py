"""overlay.html / replay.html language tables: the `en` base table is complete and Thai is not needed for English.

Runs the delimited i18n block of each page in node (skipped when node is not installed).
"""

import json
import re
import shutil
import subprocess
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
NODE = shutil.which("node") or "/opt/node22/bin/node"
THAI = re.compile("[฀-๿]")
pytestmark = pytest.mark.skipif(not Path(NODE).exists() and not shutil.which("node"), reason="node not installed")

DRIVER = """
const src = require('fs').readFileSync(0, 'utf8');
const f = new Function(src + '\\n; return { I18N, pickLang };');
const { I18N, pickLang } = f();
const shape = v => typeof v === 'function' ? 'fn' : Array.isArray(v) ? 'array' : (v && typeof v === 'object') ? Object.fromEntries(Object.keys(v).map(k => [k, shape(v[k])])) : typeof v;
const out = { shapes: {}, picks: {} };
for (const k of Object.keys(I18N)) out.shapes[k] = shape(I18N[k]);
for (const n of ['Thai', 'thai', ' THAI ', 'English', 'english', 'th', 'en', 'en-US', 'Japanese', '', null, undefined]) out.picks[String(n)] = pickLang(n);
// the English table must not contain Thai (it is what every untranslated language sees)
out.enText = JSON.stringify(I18N.en, (k, v) => typeof v === 'function' ? v.toString() : v instanceof RegExp ? v.source : v);
console.log(JSON.stringify(out));
"""


def run(page):
    html = (HERE / page).read_text(encoding="utf-8")
    m = re.search(r"// ====== i18n:begin.*?// ====== i18n:end[^\n]*", html, re.S)
    assert m, f"{page}: i18n block markers not found"
    r = subprocess.run([NODE, "-e", DRIVER], input=m.group(0), capture_output=True, text=True, encoding="utf-8", timeout=30)
    assert r.returncode == 0, r.stderr
    return json.loads(r.stdout)


def check_subset(small, big, path=""):
    for k, v in small.items():
        assert k in big, f"missing in en: {path}{k}"
        if isinstance(v, dict):
            assert isinstance(big[k], dict), f"{path}{k} is a table in one language and not in the other"
            check_subset(v, big[k], f"{path}{k}.")
        else:
            assert v == big[k], f"{path}{k}: {v} vs {big[k]}"


@pytest.mark.parametrize("page", ["overlay.html", "replay.html"])
def test_english_is_the_complete_base_table(page):
    out = run(page)
    assert "en" in out["shapes"] and "th" in out["shapes"]
    for code, shape in out["shapes"].items():
        if code != "en":
            check_subset(shape, out["shapes"]["en"])
    assert not THAI.search(out["enText"])


@pytest.mark.parametrize("page", ["overlay.html", "replay.html"])
def test_language_picking(page):
    picks = run(page)["picks"]
    assert picks["Thai"] == picks["thai"] == picks[" THAI "] == picks["th"] == "th"
    assert picks["English"] == picks["english"] == picks["en"] == picks["en-US"] == "en"
    assert picks["Japanese"] == "en"           # no table: English UI
    assert picks[""] == picks["null"] == picks["undefined"] == "th"  # unset = the mod's default


@pytest.mark.parametrize("page", ["overlay.html", "replay.html"])
def test_no_thai_outside_the_table(page):
    html = (HERE / page).read_text(encoding="utf-8")
    a, b = html.index("i18n:begin"), html.index("i18n:end")
    assert not THAI.search(html[:a] + html[b:])
