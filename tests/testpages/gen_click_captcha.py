"""生成点选验证码测试图 click-captcha.png（PIL + 系统中文字体，固定布局可复现）。

布局：
- 顶部提示行：请依次点击：苹 蕉 书（detection 检出后，StepExecutor.StripGuideChars 去引导词得 [苹,蕉,书]）
- 下方目标行：苹 蕉 书 + 干扰字 桔 瓜 笔（候选集，按提示顺序点击前 3 个）

字体：优先 vendored（tests/testpages/fonts/click-captcha.ttf），否则常见系统中文字体（simhei/msyh/wqy-zenhei）。
运行：python tests/testpages/gen_click_captcha.py
"""
import os
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(__file__), "click-captcha.png")
FONT_CANDIDATES = [
    os.path.join(os.path.dirname(__file__), "fonts", "click-captcha.ttf"),  # vendored（若有）
    r"C:\Windows\Fonts\simhei.ttf",
    r"C:\Windows\Fonts\msyh.ttc",
    "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc",
    "/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc",
]
PROMPT = list("请依次点击：") + ["苹", "蕉", "书"]   # 顶部提示行（含引导词 + 目标）
TARGETS = ["苹", "蕉", "书", "桔", "瓜", "笔"]      # 下方目标行（前 3 个为提示目标 + 3 干扰）
W, H = 520, 260


def load_font(size):
    for p in FONT_CANDIDATES:
        if p and os.path.exists(p):
            try:
                return ImageFont.truetype(p, size)
            except Exception:
                continue
    return ImageFont.load_default()


def main():
    img = Image.new("RGB", (W, H), "white")
    draw = ImageDraw.Draw(img)
    prompt_font = load_font(38)
    target_font = load_font(46)

    # 顶部提示行（y center ~50）：引导词 + 目标字，间距 ~52
    x = 20
    for ch in PROMPT:
        draw.text((x, 25), ch, font=prompt_font, fill="black")
        x += 52
    # 下方目标行（y center ~185）：目标 + 干扰，间距 ~80
    positions = [40, 130, 220, 310, 380, 450]
    for ch, px in zip(TARGETS, positions):
        draw.text((px, 160), ch, font=target_font, fill="black")

    img.save(OUT, format="PNG")
    print(f"已生成 {OUT}（{W}x{H}，顶部提示行 {len(PROMPT)} 字 + 下方目标行 {len(TARGETS)} 字）")


if __name__ == "__main__":
    main()
