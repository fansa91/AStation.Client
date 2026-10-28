#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""生成 AStation.Client 的应用图标（WinUI / Fluent 风格）。

设计思路：
  底板是 Windows 11 风格的圆角方（radius 22.5%），AcFun 品牌红对角渐变，
  压一层顶部玻璃高光、底部内阴影和 1px 白色内描边，做出「材质」而不只是平涂。
  主形是一个白色对话气泡 + 镂空的播放三角 + 弹幕条——气泡（弹幕/评论）与
  播放键合成一个符号，是这个客户端的性格，也避免读成 YouTube 的红底播放键。

用法：
  python tools/make-icon.py             # 写入 Assets/
  python tools/make-icon.py --preview    # 只生成 artifacts/icon-preview.png
"""
from __future__ import annotations

import argparse
import os

import numpy as np
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "Assets")
ARTIFACTS = os.path.join(ROOT, "artifacts")

# ─────────────────────────── 品牌色 ───────────────────────────
RED_LIGHT = (255, 138, 118)   # 左上高光
RED_MAIN = (252, 55, 66)      # AcFun 红
RED_DEEP = (211, 18, 28)      # 右下暗部

PLATE_RADIUS = 0.225
SHEEN_HEIGHT = 0.42
SHEEN_ALPHA = 0.24

# ─────────────────────────── 几何（占边长比例） ───────────────────────────
BUBBLE = (0.186, 0.182, 0.814, 0.644)   # x0, y0, x1, y1
BUBBLE_RADIUS = 0.108

# 气泡尾巴：贴底边的左下尖角，尖端单独补圆做出 Fluent 的柔和感
TAIL = [(0.304, 0.566), (0.458, 0.566), (0.304, 0.752)]
TAIL_TIP = 0.0     # 0 = 尖角；>0 时在尖端补一个圆（过大会变成棒棒糖，慎用）

# 播放三角（镂空，光学居中）
PLAY = dict(top=0.300, bot=0.528, left=0.396, apex=0.588)

# 弹幕条（镂空）；SHOW_BARS=False 时只留气泡 + 播放键
SHOW_BARS = False
BARS = [
    (0.612, 0.754, 0.336, 0.046),
    (0.612, 0.704, 0.430, 0.046),
]


def _gradient(size: int) -> np.ndarray:
    yy, xx = np.mgrid[0:size, 0:size].astype(np.float32)
    t = (xx + yy) / (2.0 * (size - 1))
    stops = [(0.0, RED_LIGHT), (0.48, RED_MAIN), (1.0, RED_DEEP)]
    out = np.zeros((size, size, 3), dtype=np.float32)
    for (p0, c0), (p1, c1) in zip(stops, stops[1:]):
        m = (t >= p0) & (t <= p1)
        local = np.clip((t - p0) / (p1 - p0), 0, 1)
        for ch in range(3):
            out[..., ch] = np.where(m, c0[ch] + (c1[ch] - c0[ch]) * local, out[..., ch])
    return out


def _rounded_mask(size: int, radius: float, inset: float = 0.0) -> Image.Image:
    ss = 4
    big = Image.new("L", (size * ss, size * ss), 0)
    pad = inset * size * ss
    ImageDraw.Draw(big).rounded_rectangle(
        [pad, pad, size * ss - 1 - pad, size * ss - 1 - pad], radius=radius * size * ss, fill=255
    )
    return big.resize((size, size), Image.LANCZOS)


def _plate(size: int, simple: bool) -> Image.Image:
    arr = np.zeros((size, size, 4), dtype=np.float32)
    arr[..., :3] = _gradient(size)
    arr[..., 3] = 255

    if not simple:
        yy = np.mgrid[0:size, 0:size][0].astype(np.float32) / (size - 1)
        sheen = np.clip((SHEEN_HEIGHT - yy) / SHEEN_HEIGHT, 0, 1) ** 1.7 * SHEEN_ALPHA
        for ch in range(3):
            arr[..., ch] += (255 - arr[..., ch]) * sheen
        shade = np.clip((yy - 0.58) / 0.42, 0, 1) ** 2 * 0.18
        for ch in range(3):
            arr[..., ch] *= (1 - shade)

    plate = Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8), "RGBA")
    plate.putalpha(_rounded_mask(size, PLATE_RADIUS))

    if not simple:
        band = max(1.0, size * 0.007)
        outer = np.asarray(_rounded_mask(size, PLATE_RADIUS), dtype=np.int16)
        inner = np.asarray(_rounded_mask(size, PLATE_RADIUS, inset=band / size), dtype=np.int16)
        ring = (np.clip(outer - inner, 0, 255) * 0.26).astype(np.uint8)
        rim = Image.new("RGBA", (size, size), (255, 255, 255, 0))
        rim.putalpha(Image.fromarray(ring, "L"))
        plate = Image.alpha_composite(plate, rim)

    return plate


def _glyph_layers(size: int, simple: bool) -> tuple[Image.Image, Image.Image]:
    """返回（白色主体遮罩, 镂空区遮罩）。"""
    ss = 8 if size <= 48 else 4
    S = size * ss
    # 16/24 px 上尾巴会糊成一个突起，直接去掉；气泡同时撑大一点，红线才不会被吃掉
    tiny = size <= 24
    grow = 0.026 if tiny else (0.014 if simple else 0.0)

    body = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(body)
    d.rounded_rectangle(
        [
            (BUBBLE[0] - grow) * S,
            (BUBBLE[1] - grow) * S,
            (BUBBLE[2] + grow) * S,
            (BUBBLE[3] + grow) * S,
        ],
        radius=(BUBBLE_RADIUS + grow * 0.4) * S,
        fill=255,
    )
    if not tiny:
        d.polygon([(x * S, y * S) for x, y in TAIL], fill=255)
    if TAIL_TIP > 0 and not tiny:
        tip = TAIL_TIP * S
        d.ellipse([TAIL[2][0] * S - tip, TAIL[2][1] * S - tip, TAIL[2][0] * S + tip, TAIL[2][1] * S + tip], fill=255)

    cut = Image.new("L", (S, S), 0)
    c = ImageDraw.Draw(cut)

    pg = 0.030 if tiny else (0.016 if simple else 0.0)
    top = (PLAY["top"] - pg) * S
    bot = (PLAY["bot"] + pg) * S
    left = (PLAY["left"] - pg * 0.9) * S
    apex = (PLAY["apex"] + pg * 1.5) * S
    c.polygon([(left, top), (left, bot), (apex, (top + bot) / 2)], fill=255)

    if SHOW_BARS:
        if simple:
            # 小尺寸只留一条更粗的弹幕条，否则会糊成噪点
            c.rounded_rectangle([0.600 * S, 0.326 * S, 0.744 * S, 0.412 * S], radius=0.043 * S, fill=255)
        else:
            for x0, x1, y0, h in BARS:
                c.rounded_rectangle([x0 * S, y0 * S, x1 * S, (y0 + h) * S], radius=(h / 2) * S, fill=255)

    return (
        body.resize((size, size), Image.LANCZOS),
        cut.resize((size, size), Image.LANCZOS),
    )


def render(size: int) -> Image.Image:
    simple = size <= 32
    plate = _plate(size, simple=simple)
    body, cut = _glyph_layers(size, simple=simple)

    b = np.asarray(body, dtype=np.int16)
    c = np.asarray(cut, dtype=np.int16)
    alpha = np.clip(b - c, 0, 255).astype(np.uint8)

    # 主体色：顶白 → 底部微暖粉，做 Fluent 的双层质感（小尺寸用纯白保证清晰）
    if simple:
        white = Image.new("RGBA", (size, size), (255, 255, 255, 255))
    else:
        yy = np.mgrid[0:size, 0:size][0].astype(np.float32) / (size - 1)
        t = np.clip((yy - 0.20) / 0.55, 0, 1)[..., None]
        top_c, bot_c = np.array([255, 255, 255], np.float32), np.array([255, 236, 232], np.float32)
        rgb = (top_c + (bot_c - top_c) * t).astype(np.uint8)
        white = Image.fromarray(np.dstack([rgb, np.full((size, size, 1), 255, np.uint8)]), "RGBA")
    white.putalpha(Image.fromarray(alpha, "L"))
    return Image.alpha_composite(plate, white)


# ─────────────────────────── 输出 ───────────────────────────
def _font(size: int):
    from PIL import ImageFont

    for name in ("segoeuib.ttf", "segoeui.ttf", "msyhbd.ttc", "msyh.ttc"):
        try:
            return ImageFont.truetype(name, size)
        except Exception:
            continue
    return None


def save_png(img: Image.Image, name: str):
    img.save(os.path.join(ASSETS, name), "PNG", optimize=True)
    print(f"  {name:52s} {img.size[0]}x{img.size[1]}")


def build_assets():
    render(1024).save(os.path.join(ASSETS, "AppIcon-1024.png"), "PNG", optimize=True)
    print("  AppIcon-1024.png")

    save_png(render(300), "Square150x150Logo.scale-200.png")
    save_png(render(88), "Square44x44Logo.scale-200.png")
    save_png(render(48), "Square44x44Logo.targetsize-48_altform-lightunplated.png")
    save_png(render(24), "Square44x44Logo.targetsize-24_altform-unplated.png")
    save_png(render(48), "LockScreenLogo.scale-200.png")
    save_png(render(50), "StoreLogo.png")

    # 宽磁贴 / 启动画面：只有图标，不写字。
    # 这两个资源是透明底、由系统按视觉元素背景色合成，白字在浅色底上会直接消失。
    wide = Image.new("RGBA", (620, 300), (0, 0, 0, 0))
    wide.alpha_composite(render(280), ((620 - 280) // 2, (300 - 280) // 2))
    wide.save(os.path.join(ASSETS, "Wide310x150Logo.scale-200.png"), "PNG", optimize=True)
    print("  Wide310x150Logo.scale-200.png")

    splash = Image.new("RGBA", (1240, 600), (0, 0, 0, 0))
    splash.alpha_composite(render(360), ((1240 - 360) // 2, (600 - 360) // 2))
    splash.save(os.path.join(ASSETS, "SplashScreen.scale-200.png"), "PNG", optimize=True)
    print("  SplashScreen.scale-200.png")

    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    render(256).save(os.path.join(ASSETS, "AppIcon.ico"), format="ICO", sizes=[(s, s) for s in sizes])
    print("  AppIcon.ico  (" + ", ".join(str(s) for s in sizes) + ")")


def preview():
    os.makedirs(ARTIFACTS, exist_ok=True)
    sizes = [256, 128, 64, 48, 32, 24, 16]
    pad, gap, big = 28, 24, 256
    row_h = big + gap
    W = pad * 2 + sum(s + gap for s in sizes)
    canvas = Image.new("RGB", (W, pad * 2 + row_h * 2), (243, 243, 243))
    ImageDraw.Draw(canvas).rectangle([0, pad + row_h, W, pad + row_h * 2], fill=(32, 32, 32))
    for row in range(2):
        y, x = pad + row * row_h, pad
        for s in sizes:
            img = render(s)
            canvas.paste(img, (x, y + (big - s) // 2), img)
            x += s + gap
    out = os.path.join(ARTIFACTS, "icon-preview.png")
    canvas.save(out, "PNG")
    print(out)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--preview", action="store_true")
    if ap.parse_args().preview:
        preview()
    else:
        build_assets()
