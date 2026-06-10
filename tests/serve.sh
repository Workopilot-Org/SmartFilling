#!/usr/bin/env bash
# 测试页静态服务（端口 8088），指向 tests/testpages
# golden 脚本统一用 http://localhost:8088/<page>.html
cd "$(dirname "$0")"
exec python -m http.server 8088 --directory testpages
