# SmartFilling v2

智能填报系统 — 基于 Playwright 确定性执行 + AI 兜底的自动化表单填写引擎。

## 项目简介

SmartFilling v2 使用 C# + Playwright 确定性执行 + AI 兜底，替代传统 AI 全流程驱动的方案，实现高效、稳定的表单自动化填写。支持录制回放、脚本编排、验证码识别等功能。

## 架构

```
SmartFilling\
├── SmartFilling.Engine\              # 引擎类库（共享）
├── SmartFilling.App\                 # 后端 API（录制 + 脚本管理）
├── SmartFilling.WebUI\               # 前端界面（纯 HTML/JS/CSS）
├── SmartFilling.BackgroundWorker\    # 后台服务（填报执行者）
├── SmartFilling.Engine.Tests\        # xUnit 测试
├── SmartFilling.App.Tests\           # App 测试
├── SmartFilling.BackgroundWorker.Tests\  # Worker 测试
└── captcha_service\                  # 验证码识别服务（Python + ddddocr）
```

## 技术栈

- .NET 8.0（所有 C# 项目）
- Microsoft.Playwright 1.59.0（浏览器自动化）
- OpenAI SDK 2.9.1（DashScope OpenAI 兼容 API）
- Jint 4.9.3（JS 条件引擎）
- FastAPI + ddddocr（验证码识别服务）

## 快速开始

### 前置要求

- .NET 8.0 SDK
- Python 3.10+
- 浏览器（Playwright 会自动下载：`playwright install`）

### 配置

1. 复制 `appsettings.Development.json.example` 为 `appsettings.Development.json`
2. 填入 DashScope API Key 和验证码服务地址

### 运行

```bash
# 启动验证码服务（可选，用于验证码识别）
cd captcha_service
pip install -r requirements.txt
python mini_server.py

# 启动后端 API
cd SmartFilling.App
dotnet run

# 启动 Worker（执行填报任务）
cd SmartFilling.BackgroundWorker
dotnet run
```

## 协议

本项目采用 **PolyForm Noncommercial License 1.0.0**。

- **非商业使用**：个人学习、研究、非盈利组织使用免费
- **商业使用**：需购买商业授权，请联系 `commercial@workopilot.com`

详见 [LICENSE](LICENSE) 和 [COMMERCIAL.md](COMMERCIAL.md)。

## 第三方依赖

| 组件 | 许可证 |
|---|---|
| Microsoft.Playwright | Apache-2.0 |
| OpenAI SDK (C#) | MIT |
| Jint | BSD-2-Clause |
| JsonSchema.Net | MIT |
| Serilog | Apache-2.0 |
| Swashbuckle.AspNetCore | MIT |
| FastAPI | MIT |
| uvicorn | BSD-3-Clause |
| ddddocr | MIT |
| Pillow | HPND |

各依赖按其原始许可证分发，本项目不改变其许可条款。