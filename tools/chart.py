#!/usr/bin/env python3
"""Draws the ladder from results/step-N.json: capacity and single-request latency, step by step.

    python3 tools/chart.py

Writes docs/ladder-light.svg and docs/ladder-dark.svg for the README. No dependencies.
Two panels, each on its own log axis: capacity spans three orders of magnitude, and latency
two, so a linear axis would flatten every step after the first.
"""
import json
import math
from html import escape
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

THEMES = {
    "light": {"surface": "#fcfcfb", "ink": "#0b0b0b", "ink2": "#52514e", "muted": "#898781", "grid": "#e1e0d9",
              "axis": "#c3c2b7", "capacity": "#257de0", "p50": "#257de0", "p95": "#f47230", "p99": "#00b894"},
    "dark": {"surface": "#1a1a19", "ink": "#f0efec", "ink2": "#c3c2b7", "muted": "#898781", "grid": "#2c2c2a",
             "axis": "#383835", "capacity": "#338ced", "p50": "#338ced", "p95": "#e76624", "p99": "#00a888"},
}
PERCENTILES = ("p50", "p95", "p99")

W = 820
LEFT, RIGHT = 72, 40
PANEL_H = 180
CAP_TOP = 156
LAT_TOP = CAP_TOP + PANEL_H + 92
H = LAT_TOP + PANEL_H + 58
FONT = 'system-ui, -apple-system, "Segoe UI", sans-serif'
TABULAR = 'style="font-variant-numeric: tabular-nums"'


def num(n):
    """1.31, 120, 2,176: two significant figures under 10, whole numbers above."""
    return f"{n:.2g}" if n < 10 else f"{round(n):,}"


def decades(values):
    return math.floor(math.log10(min(values))), math.ceil(math.log10(max(values)))


def load():
    steps = []
    for path in sorted((ROOT / "results").glob("step-[0-9].json")):
        d = json.loads(path.read_text())
        steps.append({"step": d["step"], "name": d["name"], "capacity": d["capacity"]["sustainedRate"],
                      "light": d["light"]["latencyMs"], "lightRate": d["light"]["rate"], "commit": d["commit"]})
    return sorted(steps, key=lambda s: s["step"])


def draw(steps, t):
    pw = W - LEFT - RIGHT
    band = pw / len(steps)
    x = lambda i: LEFT + band * (i + 0.5)
    first, last = steps[0], steps[-1]
    ratio = last["capacity"] / first["capacity"]
    out = []
    add = out.append

    words = {1: "One", 2: "Two", 3: "Three", 4: "Four", 5: "Five", 6: "Six", 7: "Seven", 8: "Eight", 9: "Nine"}
    title = f"{words.get(len(steps) - 1, len(steps) - 1)} changes took the API from {num(first['capacity'])} to {num(last['capacity'])} requests a second"
    sub = [
        f"Each step is the one before plus one change, on one laptop (4 cores). Capacity is the highest rate with p99",
        f"under the step's limit and almost no failures. Latency is one request at a time ({num(first['lightRate'])} a second),",
        "so it shows the work each request does, not queueing.",
    ]
    desc = f"Capacity rose {ratio:,.0f}-fold. " + "; ".join(
        f"step {s['step']}, {s['name']}: {num(s['capacity'])} requests a second, p50 {num(s['light']['p50'])} ms, "
        f"p99 {num(s['light']['p99'])} ms" for s in steps) + "."

    add(f"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{W}\" height=\"{H}\" viewBox=\"0 0 {W} {H}\" "
        f"font-family='{FONT}' role=\"img\" aria-labelledby=\"title desc\">")
    add(f'<title id="title">{escape(title)}</title><desc id="desc">{escape(desc)}</desc>')
    add(f'<rect width="{W}" height="{H}" rx="8" fill="{t["surface"]}"/>')
    add(f'<text x="{LEFT}" y="36" font-size="17" font-weight="600" fill="{t["ink"]}">{escape(title)}</text>')
    for i, line in enumerate(sub):
        add(f'<text x="{LEFT}" y="{60 + 18 * i}" font-size="13" fill="{t["ink2"]}">{escape(line)}</text>')

    def panel(top, heading, unit, values):
        lo, hi = decades(values)
        y = lambda v: top + PANEL_H - (math.log10(v) - lo) / (hi - lo) * PANEL_H
        add(f'<text x="{LEFT}" y="{top - 26}" font-size="13" font-weight="600" fill="{t["ink"]}">{escape(heading)}</text>')
        for e in range(lo, hi + 1):
            v = 10 ** e
            add(f'<line x1="{LEFT}" y1="{y(v):.1f}" x2="{LEFT + pw}" y2="{y(v):.1f}" '
                f'stroke="{t["axis"] if e == lo else t["grid"]}" stroke-width="1"/>')
            add(f'<text x="{LEFT - 8}" y="{y(v) + 4:.1f}" font-size="11" fill="{t["muted"]}" text-anchor="end" '
                f'{TABULAR}>{num(v)}{unit}</text>')
        return y

    # Capacity: one series, so no legend; the heading names it and every step is labelled.
    y = panel(CAP_TOP, "Capacity, requests a second (log scale)", "", [s["capacity"] for s in steps])
    path = " ".join(f"{'L' if i else 'M'}{x(i):.1f},{y(s['capacity']):.1f}" for i, s in enumerate(steps))
    add(f'<path d="{path}" fill="none" stroke="{t["capacity"]}" stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>')
    for i, s in enumerate(steps):
        cx, cy = x(i), y(s["capacity"])
        add(f'<circle cx="{cx:.1f}" cy="{cy:.1f}" r="4.5" fill="{t["capacity"]}" stroke="{t["surface"]}" stroke-width="2">'
            f'<title>Step {s["step"]}, {escape(s["name"])}: {num(s["capacity"])} requests a second</title></circle>')
        add(f'<text x="{cx:.1f}" y="{cy - 12:.1f}" font-size="12" font-weight="600" fill="{t["ink"]}" text-anchor="middle" '
            f'{TABULAR}>{num(s["capacity"])}</text>')

    # Latency: three percentiles per step, side by side so close values (step 0's p50 and p99 are 684 and
    # 858 ms, a hair apart on a log axis) don't hide each other. p50 left, p99 right.
    y = panel(LAT_TOP, "Latency of one request, milliseconds (log scale)", "",
              [s["light"][p] for s in steps for p in PERCENTILES])
    lx = LEFT + 360
    for j, p in enumerate(PERCENTILES):
        add(f'<circle cx="{lx + 64 * j}" cy="{LAT_TOP - 30}" r="4.5" fill="{t[p]}"/>')
        add(f'<text x="{lx + 64 * j + 10}" y="{LAT_TOP - 26}" font-size="12" fill="{t["ink2"]}">{p}</text>')
    dodge = 11
    for i, s in enumerate(steps):
        cx, l = x(i), s["light"]
        for j, p in enumerate(PERCENTILES):
            add(f'<circle cx="{cx + dodge * (j - 1):.1f}" cy="{y(l[p]):.1f}" r="4.5" fill="{t[p]}" stroke="{t["surface"]}" stroke-width="2">'
                f'<title>Step {s["step"]}: {p} {num(l[p])} ms</title></circle>')
        # The p99 is the one the pass rule uses; it's labelled on the right, the p50 on the left.
        add(f'<text x="{cx + dodge + 9:.1f}" y="{y(l["p99"]) + 4:.1f}" font-size="11" fill="{t["ink2"]}" {TABULAR}>{num(l["p99"])}</text>')
        add(f'<text x="{cx - dodge - 9:.1f}" y="{y(l["p50"]) + 4:.1f}" font-size="11" fill="{t["muted"]}" text-anchor="end" {TABULAR}>{num(l["p50"])}</text>')

    # One set of step labels, under the bottom panel; both panels share the columns.
    for i, s in enumerate(steps):
        add(f'<text x="{x(i):.1f}" y="{LAT_TOP + PANEL_H + 22}" font-size="12" font-weight="600" fill="{t["ink"]}" '
            f'text-anchor="middle">Step {s["step"]}</text>')
        add(f'<text x="{x(i):.1f}" y="{LAT_TOP + PANEL_H + 38}" font-size="11" fill="{t["ink2"]}" '
            f'text-anchor="middle">{escape(s["name"])}</text>')

    add("</svg>")
    return "\n".join(out) + "\n"


def main():
    steps = load()
    if not steps:
        raise SystemExit("No results/step-N.json yet: run scripts/ladder.py first.")
    for name, theme in THEMES.items():
        target = ROOT / "docs" / f"ladder-{name}.svg"
        target.write_text(draw(steps, theme))
        print(f"wrote {target.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
