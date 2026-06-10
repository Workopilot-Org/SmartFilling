"""mini_server 4 端点自测（直接调端点函数，无需起 HTTP 服务或装 httpx）。

运行：python test_api.py
用 captcha_service/ 下的 test_target.png / test_background.png + ../tests/testpages/captcha-text.png。
"""
import base64
import sys
import mini_server


def b64(path):
    with open(path, "rb") as f:
        return base64.b64encode(f.read()).decode()


def main():
    failures = []

    # /ocr —— 文字识别
    try:
        r = mini_server.do_ocr(mini_server.Img(image=b64("../tests/testpages/captcha-text.png")))
        assert "result" in r and isinstance(r["result"], str), r
        print(f"[OK] /ocr -> result={r['result']!r}")
    except Exception as e:
        failures.append(f"/ocr: {e!r}")

    # /slide_match —— 滑块匹配（simple_target）
    try:
        r = mini_server.do_sm(mini_server.SlideReq(
            target_image=b64("test_target.png"),
            background_image=b64("test_background.png"),
            simple_target=True))
        res = r["result"]
        assert "target_x" in res, r
        print(f"[OK] /slide_match -> target_x={res.get('target_x')}")
    except Exception as e:
        failures.append(f"/slide_match: {e!r}")

    # /slide_comparison —— 滑块比较（用同图验不崩，偏移应为 0）
    try:
        r = mini_server.do_sc(mini_server.SlideReq(
            target_image=b64("test_background.png"),
            background_image=b64("test_background.png")))
        res = r["result"]
        assert "target" in res, r
        print(f"[OK] /slide_comparison -> target={res.get('target')}")
    except Exception as e:
        failures.append(f"/slide_comparison: {e!r}")

    # /det —— 点选检测（文字图无 detection 框，验结构正确不崩）
    try:
        r = mini_server.do_det(mini_server.Img(image=b64("../tests/testpages/captcha-text.png")))
        items = r["result"]["items"]
        assert isinstance(items, list), r
        print(f"[OK] /det -> items count={len(items)}")
    except Exception as e:
        failures.append(f"/det: {e!r}")

    if failures:
        print("\n失败：")
        for f in failures:
            print(" -", f)
        sys.exit(1)
    print("\n全部 4 端点通过。")


if __name__ == "__main__":
    main()
