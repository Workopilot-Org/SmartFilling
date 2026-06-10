#!/usr/bin/env python3
"""
SmartFilling golden 套件回放器（HTTP 模式）。
- 按 tests/manifest.json 逐条 POST 脚本到 Worker /api/fill/start
- 轮询 Worker Serilog 日志判定每任务状态（Completed/Failed）+ 抓取该任务日志窗口
- 汇总写 tests/results.json，打印汇总表
用法: python tests/run_suite.py [脚本名过滤...]
"""
import json, time, os, sys, glob, urllib.request, urllib.error

# Windows 控制台默认 GBK 编码，covers/错误行含 ⑬/⓪ 等 GBK 外字符时 print 会 UnicodeEncodeError 崩溃。
# 重配 stdout/stderr 为 utf-8（errors=replace 兜底），防崩溃；results.json 用 json.dump(utf-8) 写入不受影响。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
WORKER = os.environ.get("WORKER_URL", "http://localhost:5245")
WORKER_DIR = os.path.join(REPO, "SmartFilling.BackgroundWorker")
SCRIPTS_DIR = os.path.join(REPO, "tests", "golden-scripts")
MANIFEST = os.path.join(REPO, "tests", "manifest.json")
RESULTS = os.path.join(REPO, "tests", "results.json")


def worker_log_path():
    logs = glob.glob(os.path.join(WORKER_DIR, "logs", "worker-*.log"))
    if not logs:
        return None
    return max(logs, key=os.path.getmtime)


def post_script(script_obj, fill_data, retries=8):
    body = json.dumps(
        {"documentTypeId": "gt", "documentTypeName": "golden-test",
         "scripts": [script_obj], "fillData": fill_data},
        ensure_ascii=False).encode("utf-8")
    for attempt in range(retries):
        req = urllib.request.Request(WORKER + "/api/fill/start", data=body,
                                     headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=20) as r:
                return json.loads(r.read().decode())["taskId"]
        except urllib.error.HTTPError as e:
            # 503 = 并发槽位未释放，等待重试
            if e.code == 503 and attempt < retries - 1:
                time.sleep(1.5)
                continue
            raise
        except urllib.error.URLError as e:
            if attempt < retries - 1:
                time.sleep(1.0)
                continue
            raise


def wait_task(logpath, taskid, timeout=90):
    """轮询日志直到 '任务执行结束: {taskid}' 出现；返回 (status, window_lines)。"""
    marker_end = f"任务执行结束: {taskid}"
    marker_start = f"任务开始执行: {taskid}"
    deadline = time.time() + timeout
    last_size = os.path.getsize(logpath) if logpath and os.path.exists(logpath) else 0
    while time.time() < deadline:
        time.sleep(1.0)
        if not (logpath and os.path.exists(logpath)):
            continue
        size = os.path.getsize(logpath)
        if size <= last_size:
            # 日志没增长也继续等（任务可能还在跑）
            pass
        with open(logpath, encoding="utf-8") as f:
            f.seek(0)
            text = f.read()
        if marker_end in text:
            lines = text.splitlines()
            # 找该 taskId 的窗口（最后一次 start → end）
            si = max((i for i, l in enumerate(lines) if marker_start in l), default=0)
            ei = max((i for i, l in enumerate(lines) if marker_end in l), default=len(lines) - 1)
            # 决策2 修复（E⑭ 首次实跑发现）：HttpTaskCompletionHandler 在 marker_end 之后紧接打印 "HTTP 任务完成 returnData:" 行
            # （任务结束→完成处理器 fire，约 2ms 后）。原 window=lines[si:ei+1] 截到 marker_end（不含其后）→ 漏掉 returnData →
            # expectReturnData 永远验空失败（returnData=null）。扩展窗口到 marker_end 之后若干行（不超过下一任务 marker_start），捕获 returnData。
            next_start = len(lines)
            for j in range(ei + 1, len(lines)):
                if "任务开始执行: " in lines[j]:
                    next_start = j
                    break
            window_end = min(ei + 10, next_start)
            window = lines[si:window_end]
            status = "Unknown"
            for l in window:
                if marker_end in l:
                    if "Completed" in l:
                        status = "Completed"
                    elif "Failed" in l:
                        status = "Failed"
            return status, window
    return "Timeout", []


def parse_return_data(window):
    """决策2（P28 silent-success 补强）：从日志窗口解析 'HTTP 任务完成 returnData: {JSON}'（HttpTaskCompletionHandler 打印），返回 dict 或 None。"""
    prefix = "HTTP 任务完成 returnData: "
    for l in reversed(window):  # 从后找（最后一次完成，覆盖重跑）
        if prefix in l:
            try:
                return json.loads(l.split(prefix, 1)[1].strip())
            except Exception:
                return None
    return None


def main():
    only = set(sys.argv[1:])
    manifest = json.load(open(MANIFEST, encoding="utf-8"))
    entries = manifest["scripts"]
    if only:
        entries = [e for e in entries if e["file"] in only]
    logpath = worker_log_path()
    print(f"Worker log: {logpath}")
    print(f"Running {len(entries)} scripts...\n")

    results = []
    for idx, entry in enumerate(entries, 1):
        f = entry["file"]
        path = os.path.join(SCRIPTS_DIR, f)
        # ④-4：读脚本原始 JSON 文本（不经 json.load）——Worker FillRequest.Scripts 改 List<string>，传原始文本让 Worker 执行边界 ScriptLoader 校验
        script = open(path, encoding="utf-8").read()
        fill = entry.get("fillData", {})
        expect = entry.get("expect", "completed")
        cat = entry.get("category", "deterministic")
        note = entry.get("note", "")
        covers = entry.get("covers", "")

        t0 = time.time()
        rec = {"file": f, "category": cat, "expect": expect, "covers": covers, "note": note}
        try:
            taskid = post_script(script, fill)
        except Exception as e:
            rec.update(result="START_FAIL", error=str(e), elapsed=round(time.time() - t0, 1))
            results.append(rec)
            print(f"[{idx}/{len(entries)}] START_FAIL {f}: {e}")
            continue

        status, window = wait_task(logpath, taskid)
        elapsed = round(time.time() - t0, 1)
        has_fail = any(("step 失败" in l or "任务执行失败" in l or "脚本执行失败" in l
                        or "步骤执行异常" in l) for l in window)
        if expect == "completed":
            ok = (status == "Completed") and not has_fail
        elif expect == "failed":
            ok = (status == "Failed") or has_fail
        else:
            ok = status == "Completed"
        # 决策2（P28 silent-success 补强）：解析 Worker 日志 returnData + expectReturnData 子集匹配（不只验 status，验实际返回值）
        return_data = parse_return_data(window)
        expected_rd = entry.get("expectReturnData")
        rd_ok = True
        if expected_rd:
            if not isinstance(return_data, dict):
                rd_ok = False
            else:
                for k, v in expected_rd.items():
                    if str(return_data.get(k)) != str(v):
                        rd_ok = False
                        break
            ok = ok and rd_ok
        errlines = [l for l in window if ("失败" in l or "错误" in l or "Error" in l
                                          or "异常" in l)]
        rec.update(taskid=taskid, status=status, result="PASS" if ok else "FAIL",
                   elapsed=elapsed, errors=errlines[:8], window_tail=window[-10:],
                   returnData=return_data, returnDataOk=rd_ok)
        results.append(rec)
        tag = "PASS" if ok else "FAIL"
        print(f"[{idx}/{len(entries)}] {tag}  {f}  status={status} expect={expect} {elapsed}s  ({covers})")
        if not ok:
            for l in window[-14:]:
                print("    " + l)
            print("    ---")
        time.sleep(1.0)  # 等并发槽位释放

    json.dump(results, open(RESULTS, "w", encoding="utf-8"), ensure_ascii=False, indent=2)

    # 汇总
    det = [r for r in results if r.get("category") == "deterministic"]
    det_pass = sum(1 for r in det if r["result"] == "PASS")
    all_pass = sum(1 for r in results if r["result"] == "PASS")
    print(f"\n=== 汇总: {all_pass}/{len(results)} PASS ===")
    print(f"确定性用例: {det_pass}/{len(det)} PASS")
    for r in results:
        print(f"  {r['result']:10} {r['file']:42} {r.get('status','')} {r.get('elapsed','')}s  [{r.get('category')}]")
    return 0 if all(r["result"] in ("PASS",) for r in det) else 1


if __name__ == "__main__":
    sys.exit(main())
