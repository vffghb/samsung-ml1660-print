# -*- coding: utf-8 -*-
"""
打印助手 - 图像增强/修整脚本
用法: python enhance_fix.py <输入图片> <输出图片> <调色0/1> <修整0/1>
  - 调色: 自动色阶 + 提亮 + 增强对比度 + 锐化（针对手机拍照偏暗的试卷）
  - 修整: 纸张边缘检测 + 透视矫正摆正 + 白边裁剪 + 竖向旋转判断
依赖: Pillow, opencv-python, numpy
"""
import sys
import numpy as np
from PIL import Image, ImageEnhance, ImageOps

try:
    import cv2
    HAS_CV2 = True
except ImportError:
    HAS_CV2 = False


def crop_white(img):
    """裁剪四边白色/接近白色区域，保留内容并留 2% 边距"""
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    _, mask = cv2.threshold(gray, 245, 255, cv2.THRESH_BINARY_INV)
    coords = cv2.findNonZero(mask)
    if coords is None:
        return img
    x, y, w, h = cv2.boundingRect(coords)
    if w < 20 or h < 20:
        return img
    pad_x, pad_y = int(w * 0.02), int(h * 0.02)
    x1 = max(0, x - pad_x); y1 = max(0, y - pad_y)
    x2 = min(img.shape[1], x + w + pad_x); y2 = min(img.shape[0], y + h + pad_y)
    return img[y1:y2, x1:x2]


def fix_perspective(bgr):
    """检测纸张四边形 → 透视矫正摆正 → 白边裁剪"""
    orig = bgr.copy()
    gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
    # CLAHE 自适应直方图均衡：增强暗图/阴影下纸张与背景的对比
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
    gray2 = clahe.apply(gray)
    blur = cv2.GaussianBlur(gray2, (5, 5), 0)
    # Otsu 分割：照片里纸张通常是最亮的区域
    _, otsu = cv2.threshold(blur, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    # 形态学：去噪点 + 连接纸张边缘
    otsu = cv2.morphologyEx(otsu, cv2.MORPH_OPEN, np.ones((7, 7), np.uint8))
    otsu = cv2.morphologyEx(otsu, cv2.MORPH_CLOSE, np.ones((15, 15), np.uint8))
    contours, _ = cv2.findContours(otsu, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        return crop_white(orig)
    biggest = max(contours, key=cv2.contourArea)
    if cv2.contourArea(biggest) < orig.shape[0] * orig.shape[1] * 0.2:
        return crop_white(orig)  # 检测到的区域太小，不强行矫正
    peri = cv2.arcLength(biggest, True)
    approx = cv2.approxPolyDP(biggest, 0.02 * peri, True)
    if len(approx) == 4:
        pts = np.array(approx.reshape(4, 2), dtype="float32")
        s = pts.sum(axis=1)
        tl = pts[np.argmin(s)]; br = pts[np.argmax(s)]
        diff = np.diff(pts, axis=1)
        bl = pts[np.argmin(diff)]; tr = pts[np.argmax(diff)]
        w = int(max(np.linalg.norm(tl - tr), np.linalg.norm(bl - br)))
        h = int(max(np.linalg.norm(tl - bl), np.linalg.norm(tr - br)))
        if w > 10 and h > 10:
            dst = np.array([[0, 0], [w - 1, 0], [w - 1, h - 1], [0, h - 1]], dtype="float32")
            M = cv2.getPerspectiveTransform(np.array([tl, tr, br, bl], dtype="float32"), dst)
            warped = cv2.warpPerspective(orig, M, (w, h))
            # 竖版试卷（A4 比例 1:1.414）摆正判断：
            # 若当前宽高比(w/h)比高宽比(h/w)更接近 A4 竖版比例，说明纸张被横放，旋转 90°
            a4 = 1.414
            if abs(w / h - a4) < abs(h / w - a4):
                warped = cv2.rotate(warped, cv2.ROTATE_90_CLOCKWISE)
            return crop_white(warped)
    return crop_white(orig)


def main():
    if len(sys.argv) < 3:
        print("用法: python enhance_fix.py <输入> <输出> [调色1/0] [修整1/0]")
        sys.exit(1)
    src, dst = sys.argv[1], sys.argv[2]
    do_enhance = len(sys.argv) > 3 and sys.argv[3] == '1'
    do_fix = len(sys.argv) > 4 and sys.argv[4] == '1'
    try:
        img = Image.open(src).convert('RGB')
    except Exception as e:
        print("读取失败:", e)
        sys.exit(1)
    # 顺序：先修整（用原始图像做几何检测，光照信息更真实），再调色
    if do_fix:
        if HAS_CV2:
            arr = np.array(img)
            bgr = cv2.cvtColor(arr, cv2.COLOR_RGB2BGR)
            fixed = fix_perspective(bgr)
            fixed_rgb = cv2.cvtColor(fixed, cv2.COLOR_BGR2RGB)
            img = Image.fromarray(fixed_rgb)
        else:
            print("提示: 未安装 opencv-python，跳过透视矫正")
            img = ImageOps.autocontrast(img, cutoff=1)
    if do_enhance:
        # 自动色阶（去掉暗部灰雾）
        img = ImageOps.autocontrast(img, cutoff=1)
        # 提亮 + 增强对比度 + 锐化
        img = ImageEnhance.Brightness(img).enhance(1.12)
        img = ImageEnhance.Contrast(img).enhance(1.2)
        img = ImageEnhance.Sharpness(img).enhance(1.3)
    try:
        img.save(dst)
        print("OK")
    except Exception as e:
        print("保存失败:", e)
        sys.exit(1)


if __name__ == "__main__":
    main()
