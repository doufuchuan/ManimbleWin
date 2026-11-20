"""Build a portable copy of a deck so a project can be shared without its caches.

The deck's render cache (`media/`, `deck/slides/`, `__pycache__`, partial movies)
can hold absolute paths to the previous working copy.  A portable package keeps
only the source, configuration and user assets, so no stale path leaks into it.
"""

from __future__ import annotations

import os
import shutil
import zipfile
from pathlib import Path

# Cache folders that are rebuilt by Manimble and may contain absolute paths.
CACHE_PARTS = {"__pycache__", "partial_movie_files", ".manimble", ".git"}

ENV_WARNING = ("接收方仍需自行安装 Python + manim + manim-slides + LaTeX + ffmpeg + 字体；"
               "本项目包只包含 deck 源码与资源。")


def build_portable(app, path, include_media=False) -> dict:
    """Copy the deck's sources, config and assets into `path`.

    `path` is a directory, or a file ending in `.zip` (written with stdlib zipfile).
    Returns a manifest: {ok, path, files, count, warnings}.
    """
    report = app.dependency_report()
    warnings = []
    for failure in report.get("failures", []):
        warnings.append(f"场景 {failure.get('scene')} 依赖关系扫描失败：{failure.get('error', '')}")

    try:
        deck = Path(app.deck).resolve()
        root = deck.parent
    except Exception as exc:  # noqa: BLE001
        return {"ok": False, "path": str(path), "files": [], "count": 0,
                "error": str(exc), "warnings": warnings + [ENV_WARNING]}

    files: dict[str, Path] = {}  # package-relative posix path -> source file

    def excluded(p: Path) -> bool:
        if p.suffix == ".pyc" or (set(p.parts) & CACHE_PARTS):
            return True
        try:
            rel = p.resolve().relative_to(root)
        except ValueError:
            return False
        # deck/slides/*.json is the rebuildable build cache, not source.
        return len(rel.parts) >= 2 and rel.parts[0] == deck.name and rel.parts[1] == "slides"

    def add(p: Path, rel: str | None = None) -> None:
        try:
            if not p.is_file() or excluded(p):
                return
            if rel is None:
                rel = p.resolve().relative_to(root).as_posix()
            files.setdefault(rel, p.resolve())
        except (OSError, ValueError):
            pass

    def walk(base: Path):
        if not base.is_dir():
            return
        for dirpath, dirnames, filenames in os.walk(base):
            here = Path(dirpath)
            dirnames[:] = [d for d in dirnames
                           if d not in CACHE_PARTS
                           and not (here == deck and d == "slides")]
            for name in filenames:
                yield here / name

    # 1. Source and configuration at the top of the deck folder.
    try:
        for p in deck.iterdir():
            if p.is_file() and (p.suffix == ".py" or p.name in ("manim.cfg", "order.txt")):
                add(p)
    except OSError as exc:
        warnings.append(f"无法读取 deck 目录：{exc}")

    # 2. User assets (both layouts: <root>/assets and <deck>/assets).
    for base in (root / "assets", deck / "assets"):
        for p in walk(base):
            add(p)

    # 3. Rebuildable media only when the caller asks for it.
    if include_media:
        for p in walk(root / "media"):
            add(p)

    # 4. Files opened by the deck code according to dependency_report().
    for raw in report.get("dependencies", []):
        try:
            dep = Path(raw)
            dep = dep if dep.is_absolute() else root / dep
            dep = dep.resolve()
            if dep.is_relative_to(root):
                add(dep)
                continue
            # Truly external file: its absolute reference cannot be portable.
            if not dep.is_file():
                warnings.append(f"外部依赖文件不存在：{dep}")
                continue
            candidate = f"external/{dep.name}"
            n = 1
            while candidate in files:
                candidate = f"external/{dep.stem}_{n}{dep.suffix}"
                n += 1
            files[candidate] = dep
            warnings.append(f"外部依赖 {dep} 已放入 {candidate}，但代码可能以绝对路径引用，接收方需手工改路径。")
        except (OSError, ValueError) as exc:
            warnings.append(f"处理依赖 {raw} 失败：{exc}")

    dest = Path(path)
    written: list[str] = []

    if str(dest).lower().endswith(".zip"):
        try:
            dest.parent.mkdir(parents=True, exist_ok=True)
            with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED) as zf:
                for rel in sorted(files):
                    try:
                        zf.write(files[rel], rel)
                        written.append(rel)
                    except OSError as exc:
                        warnings.append(f"无法写入 {rel}：{exc}")
        except (OSError, zipfile.BadZipFile) as exc:
            return {"ok": False, "path": str(dest), "files": [], "count": 0,
                    "error": str(exc), "warnings": warnings + [ENV_WARNING]}
    else:
        try:
            dest.mkdir(parents=True, exist_ok=True)
            for rel in sorted(files):
                target = dest / Path(rel)
                try:
                    if files[rel] == target.resolve():
                        continue
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(files[rel], target)
                    written.append(rel)
                except OSError as exc:
                    warnings.append(f"无法复制 {rel}：{exc}")
        except OSError as exc:
            return {"ok": False, "path": str(dest), "files": [], "count": 0,
                    "error": str(exc), "warnings": warnings + [ENV_WARNING]}

    warnings.insert(0, ENV_WARNING)
    return {"ok": True, "path": str(dest), "files": written,
            "count": len(written), "warnings": warnings}
