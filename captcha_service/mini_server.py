"""mini_server — 自写 FastAPI 验证码识别服务（对齐 SmartFilling.Engine CaptchaService 契约，4 端点）。

替代旧 ddddocr_server.py（魔改 1.6.0 Flask，契约不符）。
- 入口：python mini_server.py（默认 0.0.0.0:8000），或 uvicorn mini_server:app --port 8000
- 无状态、无需 initialize。
- ddddocr 1.6.1 新签名：DdddOcr(show_ad=False) / classification(bytes, png_fix=True) / detection(bytes)→List[[x1,y1,x2,y2]]。
- 契约（顶层 result 信封）：
    POST /ocr                {image:b64}                          → {result:"文字"}
    POST /det                {image:b64}                          → {result:{items:[{x,y,text}]}}（detection 框→中心点+每框 crop+classification）
    POST /slide_match        {target_image,background_image,simple_target} → {result:{target:[x,y],target_x,target_y}}
    POST /slide_comparison   {target_image,background_image}      → {result:{target:[x,y],target_x}}
"""
from fastapi import FastAPI
from pydantic import BaseModel
import ddddocr, base64, io, threading
from PIL import Image

app = FastAPI()
_lock = threading.Lock()  # §7.3：onnxruntime 非线程安全，FastAPI 同步端点跑线程池 → 加锁串行（验证码 QPS 低，无性能影响）
_ocr = ddddocr.DdddOcr(show_ad=False)                        # classification
_det = ddddocr.DdddOcr(det=True, ocr=False, show_ad=False)   # detection
_slide = ddddocr.DdddOcr(ocr=False, det=False, show_ad=False)


class Img(BaseModel):
    image: str


class SlideReq(BaseModel):
    target_image: str
    background_image: str
    simple_target: bool = False


@app.post("/ocr")
def do_ocr(r: Img):
    with _lock:
        return {"result": _ocr.classification(base64.b64decode(r.image), png_fix=True)}  # png_fix 默认开（透明 PNG 提升识别率）


@app.post("/det")
def do_det(r: Img):
    img = base64.b64decode(r.image)
    with _lock:
        boxes = _det.detection(img)
        pil = Image.open(io.BytesIO(img))
        items = []
        for x1, y1, x2, y2 in boxes:
            pad = 2  # §7.3：crop 加 pad（对齐旧 ddddocr_server），避免紧贴框切笔画边缘降识别率
            crop = pil.crop((max(0, x1 - pad), max(0, y1 - pad), x2 + pad, y2 + pad))
            buf = io.BytesIO(); crop.save(buf, format="PNG")
            items.append({"x": (x1 + x2) // 2, "y": (y1 + y2) // 2,
                          "text": _ocr.classification(buf.getvalue(), png_fix=True)})
    return {"result": {"items": items}}


@app.post("/slide_match")
def do_sm(r: SlideReq):
    with _lock:
        return {"result": _slide.slide_match(base64.b64decode(r.target_image),
                                             base64.b64decode(r.background_image),
                                             simple_target=r.simple_target)}


@app.post("/slide_comparison")
def do_sc(r: SlideReq):
    target_img = base64.b64decode(r.target_image)
    background_img = base64.b64decode(r.background_image)
    # 统一尺寸：防 DPR 舍入误差导致两图差 1-3 像素（golden 21 125% DPI 下 303x152 vs 302x153）
    from PIL import Image
    import io
    ti = Image.open(io.BytesIO(target_img))
    bi = Image.open(io.BytesIO(background_img))
    if ti.size != bi.size:
        max_w = max(ti.width, bi.width)
        max_h = max(ti.height, bi.height)
        ti = ti.resize((max_w, max_h), Image.LANCZOS)
        bi = bi.resize((max_w, max_h), Image.LANCZOS)
        buf_t = io.BytesIO(); ti.save(buf_t, format='PNG')
        buf_b = io.BytesIO(); bi.save(buf_b, format='PNG')
        target_img = buf_t.getvalue()
        background_img = buf_b.getvalue()
    with _lock:
        return {"result": _slide.slide_comparison(target_img, background_img)}


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000)
