"""BioCartoon 图标库客户端（供 RPC 调用）。

用 subprocess 调用 ``biocartoon.exe``（Rust CLI），解析 search/get/fetch 的输出。
CLI 输出通常是 JSON（``--json``），但也可能夹带下载进度等非结构化文本，
因此所有解析都做容错：优先 json.loads，失败时截取第一个 JSON 对象/数组再试。

CLI 首次运行需要下载 catalog。默认目录 (%APPDATA%/biocartoon) 在受限环境下
可能“拒绝访问”，所以这里把 APPDATA/USERPROFILE/TEMP 全部指向项目内可写目录。
首次下载可能较慢：超时设为 120s，并在错误里明确说明。

CLI 不可用（找不到 exe / 下载失败 / 目录不可写）时，search 会返回内置的
离线示例图标集（CELL/DNA/MITO 等），内置图标的 SVG/PNG 由同一组图元渲染，
保证 svg_icon.prepare 的形状比对能过，插入流水线不会因网络问题变成空壳。
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROJECT_ROOT = HERE.parent.parent            # engine/manimble/.. /.. -> project root
VENV_SCRIPTS = PROJECT_ROOT / ".venv" / "Scripts"
DATA_DIR = PROJECT_ROOT / ".data" / "biocartoon"

TIMEOUT_S = 120
_FIRST_DOWNLOAD_HINT = (
    "BioCartoon CLI 首次运行需要联网下载图标目录（catalog），约数 MB；"
    "当前网络不可达或目录不可写。已切换到内置离线示例图标。"
)


# --------------------------------------------------------------------------- CLI 定位 / 环境

def find_cli() -> str | None:
    """优先 venv 同目录的 biocartoon.exe，其次 PATH。"""
    for name in ("biocartoon.exe", "biocartoon"):
        p = VENV_SCRIPTS / name
        if p.is_file():
            return str(p)
    return shutil.which("biocartoon")


def _writable_env() -> dict:
    """子进程环境：干净、UTF-8、可写 TEMP/APPDATA。"""
    env = os.environ.copy()
    for k in ("PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP", "PYTHONEXECUTABLE",
              "PYTHONUSERBASE", "PIP_INDEX_URL", "PIP_EXTRA_INDEX_URL"):
        env.pop(k, None)
    try:
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        tmp = DATA_DIR / "tmp"
        tmp.mkdir(parents=True, exist_ok=True)
        env["APPDATA"] = str(DATA_DIR)
        env["USERPROFILE"] = str(DATA_DIR)
        env["TMP"] = env["TEMP"] = env["TMPDIR"] = str(tmp)
    except OSError:
        pass
    env["PYTHONIOENCODING"] = "utf-8"
    env["PYTHONUTF8"] = "1"
    return env


def _run(args: list[str], timeout: int = TIMEOUT_S) -> tuple[int, str, str]:
    cli = find_cli()
    if not cli:
        raise FileNotFoundError("找不到 biocartoon.exe（已在 .venv/Scripts 与 PATH 中查找）")
    try:
        cp = subprocess.run(
            [cli, *args], env=_writable_env(), timeout=timeout,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
            encoding="utf-8", errors="replace",
        )
        return cp.returncode, cp.stdout or "", cp.stderr or ""
    except subprocess.TimeoutExpired as exc:
        raise TimeoutError(f"BioCartoon CLI 超过 {timeout}s 未响应：{_FIRST_DOWNLOAD_HINT}") from exc
    except OSError as exc:
        raise RuntimeError(f"BioCartoon CLI 启动失败：{exc}") from exc


def _loose_json(text: str):
    """从可能夹带进度文本的输出中解析 JSON。"""
    if not text:
        return None
    try:
        return json.loads(text)
    except (ValueError, TypeError):
        pass
    for opener, closer in (("{", "}"), ("[", "]")):
        i = text.find(opener)
        j = text.rfind(closer)
        if i >= 0 and j > i:
            try:
                return json.loads(text[i:j + 1])
            except (ValueError, TypeError):
                continue
    return None


# --------------------------------------------------------------------------- 离线图标集

# 每个图元: (类型, 参数...)。SVG 与 PNG 都由同一组图元渲染 -> 形状一致，prepare 能过。
# 颜色只用纯填充，不含渐变/文字，符合 svg_icon.unsupported 的可绘制要求。
_ICON_DEFS = {
    "OFF-CELL": ("Cell 细胞", "builtin", "CC0-1.0", "ManimbleWin", "cell",
        [("ellipse", 50, 50, 34, 34, "#4C9BE8"), ("ellipse", 52, 48, 13, 13, "#2E5F9E"),
         ("ellipse", 44, 42, 4, 4, "#8FC2F0")]),
    "OFF-DNA": ("DNA 双螺旋", "builtin", "CC0-1.0", "ManimbleWin", "helix",
        [("line", 30, 12, 70, 88, "#7C5CFF", 5), ("line", 70, 12, 30, 88, "#FF6B9A", 5),
         ("line", 38, 26, 62, 26, "#CCCCCC", 3), ("line", 46, 44, 54, 44, "#CCCCCC", 3),
         ("line", 46, 56, 54, 56, "#CCCCCC", 3), ("line", 38, 74, 62, 74, "#CCCCCC", 3)]),
    "OFF-MITO": ("Mitochondrion 线粒体", "builtin", "CC0-1.0", "ManimbleWin", "mito",
        [("ellipse", 50, 50, 42, 26, "#E8743B"),
         ("line", 30, 40, 40, 56, "#F7C59F", 4), ("line", 44, 38, 52, 58, "#F7C59F", 4),
         ("line", 58, 40, 66, 58, "#F7C59F", 4)]),
    "OFF-MOL": ("Molecule 分子", "builtin", "CC0-1.0", "ManimbleWin", "molecule",
        [("line", 28, 50, 72, 50, "#888888", 5), ("line", 50, 28, 50, 72, "#888888", 5),
         ("ellipse", 28, 50, 12, 12, "#4CAF50"), ("ellipse", 72, 50, 12, 12, "#2196F3"),
         ("ellipse", 50, 28, 10, 10, "#FF9800"), ("ellipse", 50, 72, 10, 10, "#F44336")]),
    "OFF-NEURON": ("Neuron 神经元", "builtin", "CC0-1.0", "ManimbleWin", "neuron",
        [("ellipse", 50, 52, 18, 18, "#9C27B0"), ("ellipse", 50, 52, 7, 7, "#E1BEE7"),
         ("line", 50, 34, 34, 14, "#9C27B0", 4), ("line", 50, 34, 50, 10, "#9C27B0", 4),
         ("line", 50, 34, 68, 14, "#9C27B0", 4), ("line", 68, 52, 90, 62, "#9C27B0", 4),
         ("line", 68, 58, 92, 84, "#9C27B0", 4)]),
    "OFF-VIRUS": ("Virus 病毒", "builtin", "CC0-1.0", "ManimbleWin", "virus",
        [("ellipse", 50, 50, 28, 28, "#F44336"), ("ellipse", 50, 50, 10, 10, "#FFCDD2"),
         ("line", 50, 22, 50, 8, "#F44336", 4), ("line", 50, 78, 50, 92, "#F44336", 4),
         ("line", 22, 50, 8, 50, "#F44336", 4), ("line", 78, 50, 92, 50, "#F44336", 4),
         ("line", 30, 30, 20, 20, "#F44336", 4), ("line", 70, 70, 80, 80, "#F44336", 4)]),
    "OFF-HEART": ("Heart 心脏", "builtin", "CC0-1.0", "ManimbleWin", "heart",
        [("ellipse", 40, 42, 20, 20, "#D32F2F"), ("ellipse", 60, 42, 20, 20, "#D32F2F"),
         ("polygon", "#D32F2F", (22, 50), (78, 50), (50, 88))]),
    "OFF-BACTERIA": ("Bacterium 细菌", "builtin", "CC0-1.0", "ManimbleWin", "bacteria",
        [("ellipse", 50, 50, 38, 18, "#3F51B5"),
         ("line", 12, 50, 2, 38, "#3F51B5", 3), ("line", 12, 54, 2, 66, "#3F51B5", 3),
         ("line", 88, 50, 98, 40, "#3F51B5", 3), ("ellipse", 34, 50, 6, 6, "#C5CAE9")]),
    "OFF-PROTEIN": ("Protein 蛋白质", "builtin", "CC0-1.0", "ManimbleWin", "protein",
        [("line", 20, 70, 40, 30, "#009688", 7), ("line", 40, 30, 60, 70, "#009688", 7),
         ("line", 60, 70, 80, 30, "#009688", 7)]),
    "OFF-ATOM": ("Atom 原子", "builtin", "CC0-1.0", "ManimbleWin", "atom",
        [("ellipse", 50, 50, 5, 5, "#FFEB3B"),
         ("ring", 50, 50, 36, 12, 0, "#00BCD4", 3),
         ("ring", 50, 50, 36, 12, 60, "#00BCD4", 3),
         ("ring", 50, 50, 36, 12, 120, "#00BCD4", 3)]),
}


def _svg_of(prims) -> str:
    out = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100">']
    for p in prims:
        t = p[0]
        if t == "ellipse":
            _, cx, cy, rx, ry, c = p
            out.append(f'<ellipse cx="{cx}" cy="{cy}" rx="{rx}" ry="{ry}" fill="{c}"/>')
        elif t == "rect":
            _, x, y, w, h, c = p
            out.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" fill="{c}"/>')
        elif t == "line":
            _, x1, y1, x2, y2, c, w = p
            out.append(f'<line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" stroke="{c}" stroke-width="{w}"/>')
        elif t == "polygon":
            _, c, *pts = p
            ps = " ".join(f"{x},{y}" for x, y in pts)
            out.append(f'<polygon points="{ps}" fill="{c}"/>')
        elif t == "ring":
            _, cx, cy, rx, ry, deg, c, w = p
            out.append(f'<ellipse cx="{cx}" cy="{cy}" rx="{rx}" ry="{ry}" fill="none" '
                       f'stroke="{c}" stroke-width="{w}" transform="rotate({deg} {cx} {cy})"/>')
    out.append("</svg>")
    return "\n".join(out)


def _png_of(prims, path: str, side: int = 256) -> None:
    from PIL import Image, ImageDraw
    s = side / 100.0
    img = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    for p in prims:
        t = p[0]
        if t == "ellipse":
            _, cx, cy, rx, ry, c = p
            d.ellipse([(cx - rx) * s, (cy - ry) * s, (cx + rx) * s, (cy + ry) * s], fill=c)
        elif t == "rect":
            _, x, y, w, h, c = p
            d.rectangle([x * s, y * s, (x + w) * s, (y + h) * s], fill=c)
        elif t == "line":
            _, x1, y1, x2, y2, c, w = p
            d.line([x1 * s, y1 * s, x2 * s, y2 * s], fill=c, width=max(1, int(w * s)))
        elif t == "polygon":
            _, c, *pts = p
            d.polygon([(x * s, y * s) for x, y in pts], fill=c)
        elif t == "ring":
            _, cx, cy, rx, ry, deg, c, w = p
            import math
            ang = math.radians(deg)
            pts = []
            for k in range(60):
                a = 2 * math.pi * k / 60
                ex, ey = rx * math.cos(a), ry * math.sin(a)
                pts.append(((cx + ex * math.cos(ang) - ey * math.sin(ang)) * s,
                            (cy + ex * math.sin(ang) + ey * math.cos(ang)) * s))
            d.line(pts + [pts[0]], fill=c, width=max(1, int(w * s)), joint="curve")
    img.save(path)


def _offline_items(query: str, limit: int) -> list[dict]:
    q = (query or "").strip().lower()
    items = []
    for iid, (title, src, lic, author, kind, prims) in _ICON_DEFS.items():
        if q and q not in title.lower() and q not in kind and q not in iid.lower():
            continue
        items.append({
            "id": iid, "title": title, "source": src, "source_name": "内置示例",
            "license": lic, "author": author, "credit": f'"{title}" by {author}, ManimbleWin, {lic}',
            "description": "离线示例图标（CLI 不可用时使用）", "tags": [kind],
            "width": 100, "height": 100, "embedded": True,
            "thumb_url": "", "svg_url": "", "url": "",
        })
        if len(items) >= limit:
            break
    return items


def _offline_fetch(iid: str, out: str, kind: str) -> dict:
    d = _ICON_DEFS.get(iid)
    if not d:
        return {"ok": False, "error": f"未知的内置图标：{iid}"}
    prims = d[5]
    try:
        Path(out).parent.mkdir(parents=True, exist_ok=True)
        if kind == "svg":
            Path(out).write_text(_svg_of(prims), encoding="utf-8")
        else:
            _png_of(prims, out)
        return {"ok": True, "file": out}
    except Exception as exc:  # noqa: BLE001
        return {"ok": False, "error": f"生成内置图标失败：{exc}"}


# --------------------------------------------------------------------------- 对外 API

def search(query: str, limit: int = 12, filters: dict | None = None) -> dict:
    """{ok, items, error, source}。CLI 不可用时回退到内置图标集（source=offline）。"""
    limit = max(1, min(int(limit or 12), 60))
    filters = filters or {}
    args = ["search", query or "", "--icons", "--file-type", "svg",
            "--limit", str(limit), "--json"]
    for key, flag in (("source", "--source"), ("license", "--license"),
                      ("author", "--author"), ("category", "--category"), ("tags", "--tags")):
        v = filters.get(key)
        if v:
            args += [flag, str(v)]
    try:
        rc, out, err = _run(args)
        data = _loose_json(out)
        raw = None
        if isinstance(data, dict):
            raw = data.get("icons") or data.get("items") or data.get("results")
        elif isinstance(data, list):
            raw = data
        if rc != 0 or raw is None:
            detail = (err or out or "").strip().splitlines()
            reason = detail[-1] if detail else "CLI 未返回可解析的结果"
            raise RuntimeError(reason)
        items = []
        for it in raw:
            if not isinstance(it, dict):
                continue
            iid = str(it.get("id") or "").strip()
            if not iid:
                continue
            items.append({
                "id": iid,
                "title": it.get("title") or iid,
                "source": it.get("source") or "",
                "source_name": it.get("source_name") or it.get("source") or "",
                "license": it.get("license") or "",
                "author": it.get("author") or "",
                "credit": it.get("credit") or "",
                "description": it.get("description") or "",
                "tags": it.get("tags") or [],
                "width": it.get("width"), "height": it.get("height"),
                "thumb_url": it.get("thumb_url") or "",
                "svg_url": it.get("svg_url") or "",
                "url": it.get("url") or "",
                "embedded": False,
            })
        return {"ok": True, "items": items, "error": None, "source": "cli"}
    except Exception as exc:  # noqa: BLE001
        msg = str(exc).strip()
        # 过滤掉 CLI 进度/JSON 残片（如单独的 '}'），给用户一句可读的原因
        if len(msg) < 8 or msg in ("}", "]", "{", "["):
            msg = "CLI 未返回该关键词的结果"
        hint = _FIRST_DOWNLOAD_HINT if ("拒绝访问" in msg or "Timeout" in msg or "超时" in msg
                                        or "download" in msg.lower() or "CLI" in msg) else msg
        return {"ok": True, "items": _offline_items(query, limit),
                "error": hint, "source": "offline"}


def get(iid: str) -> dict:
    """{ok, item, error}。内置图标也可 get。"""
    if iid in _ICON_DEFS:
        title, src, lic, author, kind, _ = _ICON_DEFS[iid]
        return {"ok": True, "item": {"id": iid, "title": title, "source": src,
                "source_name": "内置示例", "license": lic, "author": author,
                "credit": f'"{title}" by {author}, ManimbleWin, {lic}',
                "description": "离线示例图标", "tags": [kind], "embedded": True}}
    try:
        rc, out, err = _run(["get", iid, "--json"])
        data = _loose_json(out)
        if rc != 0 or not isinstance(data, dict):
            return {"ok": False, "error": (err or out or "CLI 未返回图标信息").strip()[-300:]}
        return {"ok": True, "item": data}
    except Exception as exc:  # noqa: BLE001
        return {"ok": False, "error": str(exc)}


def fetch(iid: str, out: str, kind: str = "svg") -> dict:
    """下载图标的 SVG 或 PNG 到 out。kind: svg|png。返回 {ok, file, error}。"""
    kind = "png" if str(kind).lower() == "png" else "svg"
    if iid in _ICON_DEFS:
        return _offline_fetch(iid, out, kind)
    args = ["fetch", iid, "--json", "--out", out]
    args += ["--png", "--max-side", "256"] if kind == "png" else ["--svg"]
    try:
        rc, out_text, err = _run(args)
        data = _loose_json(out_text)
        if rc == 0 and (Path(out).is_file() or (isinstance(data, dict) and Path(str(data.get("path", out))).is_file())):
            return {"ok": True, "file": out}
        return {"ok": False, "error": (err or out_text or "下载失败").strip()[-300:]}
    except Exception as exc:  # noqa: BLE001
        return {"ok": False, "error": str(exc)}


if __name__ == "__main__":                       # 便于手工自检
    print("CLI:", find_cli())
    print(json.dumps(search("cell", 3), ensure_ascii=False, indent=2)[:1200])
