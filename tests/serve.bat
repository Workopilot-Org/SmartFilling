@echo off
REM 测试页静态服务（端口 8088），指向 tests\testpages
cd /d "%~dp0"
python -m http.server 8088 --directory testpages
