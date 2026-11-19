"""Shared base class and constants for the hello deck."""

import platform

from manim import *
from manim_slides import Slide

# Windows 上没有 Helvetica Neue，Text() 指定缺失字体会渲染失败，按平台选一个可用字体。
FONT = "Segoe UI" if platform.system() == "Windows" else "Helvetica Neue"
ACCENT = "#7C5CFF"


class Deck(Slide):
    """Every slide of the deck derives from this."""

    skip_reversing = True


def label(text, size=30, color=WHITE, **kwargs):
    """A text label in the deck font."""
    return Text(text, font=FONT, font_size=size, color=color, **kwargs)
