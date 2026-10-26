# -*- coding: utf-8 -*-
"""生成 SamsungPrinter 应用图标 app.ico：蓝色圆角方块 + 白色“印”字，多尺寸。"""
import os
import sys
from PIL import Image, ImageDraw, ImageFont

OUT = r"D:\SamsungML1660Print\app.ico"
SIZES = [16, 24, 32, 48, 64, 128, 256]


def pick_font():
    candidates = [
        r"C:\Windows\Fonts\msyhbd.ttc",   # 微软雅黑 粗体
        r"C:\Windows\Fonts\msyh.ttc",
        r"C:\Windows\Fonts\simhei.ttf",
        r"C:\Windows\Fonts\msyhbd.ttf",
    ]
    for c in candidates:
        if os.path.exists(c):
            return c
    return None


def render(size, font_path):
    scale = 4
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    radius = int(s * 0.24)
    # 主色 #2563EB
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=radius, fill=(37, 99, 235, 255))
    if font_path:
        f = ImageFont.truetype(font_path, int(s * 0.60))
        bbox = d.textbbox((0, 0), "印", font=f)
        tw = bbox[2] - bbox[0]
        th = bbox[3] - bbox[1]
        x = (s - tw) / 2 - bbox[0]
        y = (s - th) / 2 - bbox[1] - int(s * 0.02)
        d.text((x, y), "印", font=f, fill=(255, 255, 255, 255))
    return img.resize((size, size), Image.LANCZOS)


def main():
    fp = pick_font()
    images = [render(s, fp) for s in SIZES]
    # 最大的作为基准图，其余尺寸作为已渲染好的帧，保证小尺寸下也清晰
    base = images[-1]
    base.save(
        OUT,
        format="ICO",
        append_images=images[:-1],
        sizes=[(s, s) for s in SIZES],
    )
    print("OK", OUT, os.path.getsize(OUT), "bytes")


if __name__ == "__main__":
    main()
