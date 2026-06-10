# mini_server 验证码识别服务

自写 FastAPI 验证码识别服务，对齐 SmartFilling.Engine `CaptchaService` 契约（4 端点）。

替代旧 `ddddocr_server.py`（魔改 1.6.0 Flask，契约不符、端口 5000）。基于干净的 ddddocr 1.6.1（新签名）。

## 功能

- ✅ **文字验证码**（text）：`/ocr` 识别文字/数字
- ✅ **滑块验证码**（slide）：`/slide_match` 缺口匹配（simple_target）
- ✅ **像素差异滑块**（pixel）：`/slide_comparison` 两图对比找缺口
- ✅ **点选验证码**（click）：`/det` detection + 每框 crop + classification，返回 `{x,y,text}` 中心点

## 架构

```
┌─────────────────────────────┐    HTTP JSON (base64)    ┌──────────────────────────┐
│  SmartFilling.Engine        │ ◄──────────────────────► │  mini_server (Python)    │
│   CaptchaService.cs         │   /ocr /det              │  FastAPI + ddddocr 1.6.1 │
│   (4 方法，CancellationToken)│   /slide_match           │  port 8000               │
└─────────────────────────────┘   /slide_comparison      └──────────────────────────┘
            │
            └─► StepExecutor ExecuteCaptchaAsync（text/slide/pixel/click 4 型）
```

## 快速开始

```bash
cd captcha_service
# 方式一：启动脚本（自动建 venv + 装依赖 + 启动）
start_server.bat
# 方式二：手动
pip install --upgrade -r requirements.txt
python mini_server.py          # 监听 0.0.0.0:8000
# 或
uvicorn mini_server:app --port 8000
```

## 配置 .NET 端

`SmartFilling.App/appsettings.json` 与 `SmartFilling.BackgroundWorker/appsettings.json`（默认已配置）：

```json
{
  "CaptchaService": { "Url": "http://localhost:8000" }
}
```

## API 契约（顶层 `result` 信封）

| 路由 | 请求体 | 响应 |
|---|---|---|
| `POST /ocr` | `{image:b64}` | `{result:"文字"}`（内部 `classification(png_fix=True)`） |
| `POST /det` | `{image:b64}` | `{result:{items:[{x,y,text}]}}`（detection 框→中心点 + 每框 crop + classification） |
| `POST /slide_match` | `{target_image, background_image, simple_target}` | `{result:{target:[x,y], target_x, target_y}}` |
| `POST /slide_comparison` | `{target_image, background_image}` | `{result:{target:[x,y], target_x}}` |

> 无状态、无需 `/initialize`。健康检查：`GET /openapi.json`（200）。
> `png_fix` 默认开（透明 PNG 提升识别率）。onnxruntime 非线程安全 → 全局 `threading.Lock` 串行（验证码 QPS 低，无性能影响）。

## 点选（click）补全契约

`/det` 返回每个目标框的中心点 `{x,y}` + classification 文字 `text`。Engine `StepExecutor` 的 click case：
- **路径 A**（脚本提供 `targetSelector`）：读提示元素 `textContent`（DOM 最准）；空（canvas）则截图 OCR；去引导词 + 分割得字序列。
- **路径 B**（无 `targetSelector`，提示在图内）：按 Y 取顶部一行按 X 排序 = 提示序列。
- 按序列匹配 `detection.Items[].Text`，叠加 `imageSelector` 的 `BoundingBox` 偏移后 `Mouse.Click`（图像像素→页面坐标）。

## 故障排查

- **无法连接**：确认 `python mini_server.py` 已起、端口 8000 未占用；`GET http://localhost:8000/openapi.json` 应 200。
- **识别失败**：检查验证码图是否清晰截取；点选/滑块特殊场景可由 Engine 走 retry / AI fallback（`AiActionPrompts` 已含 captcha 引导）。
- **依赖装不上**：`pip install --upgrade -r requirements.txt -i https://pypi.tuna.tsinghua.edu.cn/simple`（venv 已存在时 `--upgrade` 确保升到 1.6.1）。

## 技术栈

- Python 3.8+（实测 3.11.9）、FastAPI + uvicorn、ddddocr 1.6.1、Pillow
- 容器部署建议 Linux Docker（自写 Dockerfile ~10 行，无需中文字体——推理非渲染）

## 文件结构

```
captcha_service/
├── mini_server.py        # FastAPI 主服务（4 端点，对齐 Engine 契约）
├── requirements.txt      # 依赖（FastAPI/uvicorn/pydantic/ddddocr[api]==1.6.1/Pillow）
├── start_server.bat      # Windows 启动脚本（venv + 依赖 + 启动）
├── test_api.py           # 4 端点自测
├── test_target.png       # 滑块联调用测试图
└── test_background.png   # 滑块联调用测试图
```

> 旧 `ddddocr_server.py`（魔改 Flask，契约不符）已删除，可在 `git checkout 404b283 -- captcha_service/` 找回。
