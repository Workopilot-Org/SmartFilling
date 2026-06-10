# Golden 套件覆盖矩阵

回放日期 2026-06-25｜HTTP 模式｜Worker `POST /api/fill/start`（直连，不经 App）
图例：✓=回放通过｜脚本列指向 `tests/golden-scripts/<file>`｜类别：**D**=确定性(0 token) **S**=半确定性 **A**=需服务(非确定性,烧 token)

## 1. Action 覆盖（22 种，全覆盖）

| action | 脚本 | 类别 | 结果 | 说明 |
|---|---|:--:|:--:|---|
| navigate | 01,04,05,07,08,09,10,11,12,13,14,15,16,17,18,19-24 | D | ✓ | 各脚本首步 |
| click | 03,05(doubleClick/useLast),08,10,18(开新标签) | D | ✓ | 含 doubleClick/useLast |
| fill | 02,04,05,07(iframe),10(loop) | D | ✓ | 含 {{}} 变量替换 |
| type | 05 | D | ✓ | PressSequentially 逐字 |
| select | 02(value),05(label/index) | D | ✓ | matchBy 三种 |
| hover | 05 | D | ✓ | mouseenter |
| pressKey | 04(Enter 提交) | D | ✓ | 表单回车提交 |
| scroll | 05(direction bottom),26(selector 滚到元素 + down/bottom/top/up/left/right) | D | ✓ | direction 模式 + selector 模式（gt-26 补齐 selector-only P30 缺口） |
| upload | 09(单路径 + 数组) | D | ✓ | SetInputFiles；09 的 F_FILES 是**字符串数组**，**生产对象数组形态（WorkerApiClient.ConvertAttachmentUrls 产出的 `List<object>` of Dictionary）未 E2E 覆盖**，见 WorkerApiClientTests |
| evaluate | 06,11,15,16 | D | ✓ | JS 求值 + storeAs |
| extract | 06(url/title/value/textContent/count/checked/regex),18(url) | D | ✓ | 全 extractType + property |
| goBack | 17 | D | ✓ | 浏览器后退 |
| reload | 17 | D | ✓ | 刷新 |
| switchTab | 18 | D | ✓ | 新标签切换 |
| closeTab | 18 | D | ✓ | 关标签 |
| wait | 01(ms),17(until selector_visible) | D | ✓ | ms + until 两种 |
| check | 几乎全部 | D | ✓ | 配合所有 detect |
| handleDialog | 08(延迟弹窗) | D | ✓ | accept |
| screenshot | 01,06,17(viewport/fullPage/element/storeContent path/dataUrl/both) | D | ✓ | 全变体（触发 ③ schema 修复） |
| ai | 23(action),24(phase) | A | ✓ | DashScope 自主执行 |
| captcha | 19(text),20(slide),21(pixel),22(click) | S | ✓ | 经 mini_server（captcha_service/mini_server.py，8000） |
| goto | 11(toPhase) | D | ✓ | 跳过中间 phase |

## 2. then 覆盖（11 种，全覆盖）

| then | 脚本 | 类别 | 结果 | 说明 |
|---|---|:--:|:--:|---|
| nothing | 01,02,…(大量) | D | ✓ | no-op 通过 |
| continue | 10(loop new_row_appears) | D | ✓ | 下一行迭代 |
| goto | 11(check then goto toPhase) | D | ✓ | 跳转 |
| step_error | 14(step onError skip 恢复) | D | ✓ | 按本 step onError |
| phase_success | 11(提前结束当前 phase) | D | ✓ | 跳过当前 phase 后续 step |
| phase_error | 14(phase onError skip 恢复) | D | ✓ | 触发 phase onError |
| phase_rerun | 15(计数器驱动重跑) | D | ✓ | 重跑整个 phase |
| row_rerun | 16(loop 内重跑当前行) | D | ✓ | 重跑当前行 |
| script_success | 12(提前结束整个脚本) | D | ✓ | 后续 phase 不执行 |
| script_fail | 13(脚本失败终止) | D | ✓(预期 failed) | expect=failed |
| break | 16(loop 退出) | D | ✓ | 退出循环 |

## 3. detect 覆盖（19 种已覆盖 + 组合；document_ready 为 #1 新增第 20 种，无 golden 覆盖见 TD-P5 留后续）

| detect | 脚本 | 结果 |
|---|---|:--:|
| url_changed | 04 | ✓ |
| url_contains | 01,04,17,18 | ✓ |
| selector_visible | 04,05,17(until) | ✓ |
| selector_exists | 17 | ✓ |
| selector_gone | 17 | ✓ |
| selector_enabled | 17 | ✓ |
| iframe_exists | 07 | ✓ |
| page_exists | 18 | ✓ |
| dialog_contains | 08 | ✓ |
| page_contains | 17,18 | ✓ |
| selector_value | 02,05,07,19 | ✓ |
| selector_text | 02,03,05,08,10,17,20,21 | ✓ |
| selector_checked | 05 | ✓ |
| selector_selected | 05 | ✓ |
| selector_count | 05,09,10,17 | ✓ |
| new_row_appears | 10 | ✓ |
| js | 06,15,16 | ✓ |
| data_exists | 17 | ✓ |
| always | 11,12,13,14 | ✓ |
| **组合 all** | 08,17 | ✓ |
| **组合 any / not** | 17(any),03/17(not) | ✓ |

## 4. 场景覆盖

| 场景 | 页面 | 脚本 | 结果 |
|---|---|---|:--:|
| 登录全流程 | login.html | 01,04 | ✓ |
| 基础表单 | form-basic.html | 02,03,05 | ✓ |
| 数据提取/返回 | extract-result.html | 06 | ✓ |
| 嵌套 iframe | iframe-nested/inner/deep.html | 07 | ✓ |
| 原生对话框 | dialog.html | 08 | ✓ |
| 文件上传 | upload.html | 09 | ✓ |
| 表格循环 | table-loop.html | 10 | ✓ |
| 控制流 | control.html | 11-16 | ✓ |
| 多标签页 | multi-tab.html | 18 | ✓ |
| 导航回退/刷新 | login/form-basic | 17 | ✓ |
| 验证码(4型) | captcha.html | 19-22 | ✓(S) |
| AI 自主 | form-basic | 23,24 | ✓(A) |

## 5. 字段特性覆盖

- 变量替换 `{{var}}`：02(F_NAME),09(F_FILE/F_FILES 数组),10(DETAILS 嵌套行/{{name}}/{{qty}})
- storeAs 单/多变量：06,15,16,17(screenshot storeContent both/dataUrl)
- transform/format：未单独设场景（fields 用 string/number/file/array，无 date/format 场景；属可选覆盖）
- source=system：未单独场景（AI 收集层特性，App 侧 chat 路径，非 Worker 填报路径）
- skipIfDataEmpty / skipIfElementMissing：未单独场景（容错修饰符，引擎已单测覆盖）
- fallback / retry：未单独场景（容错层，引擎已单测覆盖；08 用 preSetup，05/17 用 retry 间接）

## 6. 汇总

| 类别 | 数量 | 通过 |
|---|:--:|:--:|
| 确定性 D | 18 | 18 ✓ |
| 半确定性 S (captcha) | 4 | 4 ✓ |
| 需服务 A (ai) | 2 | 2 ✓（单跑，非确定性） |
| **合计** | **24** | **24 ✓** |

ScriptLoader 严格校验（schema + 业务）：**24/24 通过**（含 ③ schema 修复后）。
