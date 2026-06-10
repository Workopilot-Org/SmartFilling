@echo off
REM mini_server 验证码识别服务启动脚本（FastAPI + ddddocr 1.6.1，对齐 SmartFilling.Engine CaptchaService 契约）
REM 使用方法: 双击运行或在命令行中执行 start_server.bat

echo ========================================
echo   mini_server 验证码识别服务启动脚本
echo ========================================
echo.

REM 检查 Python 是否安装
python --version >nul 2>&1
if errorlevel 1 (
    echo [错误] 未找到 Python，请先安装 Python 3.8 或更高版本
    echo 下载地址: https://www.python.org/downloads/
    pause
    exit /b 1
)

echo [1/4] 检测到 Python 版本:
python --version
echo.

REM 检查虚拟环境是否存在
if not exist "venv" (
    echo [2/4] 创建虚拟环境...
    python -m venv venv
    if errorlevel 1 (
        echo [错误] 创建虚拟环境失败
        pause
        exit /b 1
    )
    echo [完成] 虚拟环境创建成功
) else (
    echo [2/4] 虚拟环境已存在，跳过创建
)
echo.

REM 激活虚拟环境
echo [3/4] 激活虚拟环境...
call venv\Scripts\activate.bat
if errorlevel 1 (
    echo [错误] 激活虚拟环境失败
    pause
    exit /b 1
)
echo.

REM 安装或更新依赖（§7.7：venv 已存在时 --upgrade 确保旧 ddddocr 升到 1.6.1）
echo [3/4] 检查并安装依赖...
pip install --upgrade -r requirements.txt -i https://pypi.tuna.tsinghua.edu.cn/simple
if errorlevel 1 (
    echo [警告] 依赖安装可能存在问题，尝试继续启动...
)
echo.

REM 启动服务
echo [4/4] 启动 mini_server 验证码识别服务...
echo.
echo ========================================
echo   服务信息
echo ========================================
echo   服务地址: http://localhost:8000
echo   健康检查: http://localhost:8000/openapi.json
echo   端点: /ocr /det /slide_match /slide_comparison
echo.
echo   按 Ctrl+C 停止服务
echo ========================================
echo.

python mini_server.py

REM 服务停止后暂停
pause
