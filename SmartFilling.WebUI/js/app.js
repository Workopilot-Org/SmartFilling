// ========== v2 SmartFilling WebUI ==========
// 从 Demo app.js 迁移，去掉本地/远程选择，适配 v2 API + 双 Hub SignalR

// App Hub 连接（录制/管理/RequestHelp）
const appConnection = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/agent")
    .withAutomaticReconnect()
    .build();

// Worker Hub 连接（填报进度/截图，按需创建）
let workerConnection = null;

let currentTaskId = null;
let currentScriptData = null;
let currentTab = 'record';
let isStoppedByUser = false;

// 文件存储
const recordFiles = [];
const fillFiles = [];

// UI Elements
const tabs = document.querySelectorAll('.tab');
const panels = {
    record: document.getElementById('record-panel'),
    fill: document.getElementById('fill-panel'),
    'doc-manager': document.getElementById('doc-manager')
};
const mainElem = document.querySelector('main');
const chatHistory = document.getElementById('chat-history');
const chatInput = document.getElementById('chat-input');
const btnSend = document.getElementById('btn-send');

const DEFAULT_PLACEHOLDER = "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0MDAiIGhlaWdodD0iMjAwIj48cmVjdCB3aWR0aD0iMTAwJSIgaGVpZ2h0PSIxMDAlIiBmaWxsPSIjZjFmNWY5Ii8+PHRleHQgeD0iNTAlIiB5PSI1MCUiIGZpbGw9IiM5NGEzYjgiIGRvbWluYW50LWJhc2VsaW5lPSJtaWRkbGUiIHRleHQtYW5jaG9yPSJtaWRkbGUiIGZvbnQtZmFtaWx5PSJzYW5zLXNlcmlmIj7mmoLml6DmiKrlm748L3RleHQ+PC9zdmc+";

const previews = {
    record: {
        panel: document.getElementById('record-preview'),
        logs: document.getElementById('record-logs'),
        screenshot: document.getElementById('record-screenshot'),
        statusBadge: document.getElementById('record-status-badge'),
        stopBtn: document.getElementById('btn-stop-record'),
    },
    fill: {
        panel: document.getElementById('fill-preview'),
        logs: document.getElementById('fill-logs'),
        screenshot: document.getElementById('fill-screenshot'),
        statusBadge: document.getElementById('fill-status-badge'),
        resultText: document.getElementById('fill-result-text'),
        stopBtn: document.getElementById('btn-stop-fill'),
    }
};

// ========== Tab 切换 ==========
tabs.forEach(tab => {
    tab.addEventListener('click', () => {
        tabs.forEach(t => t.classList.remove('active'));
        tab.classList.add('active');
        currentTab = tab.dataset.tab;

        Object.keys(panels).forEach(key => {
            panels[key].classList.toggle('hidden', key !== currentTab);
        });

        // 显隐对应的预览面板
        previews.record.panel.classList.toggle('hidden', currentTab !== 'record');
        previews.fill.panel.classList.toggle('hidden', currentTab !== 'fill');

        if (currentTab === 'doc-manager') {
            mainElem.style.gridTemplateColumns = '1fr';
            loadDocManagerList();
        } else {
            mainElem.style.gridTemplateColumns = '400px 1fr';
            // 切回面板时滚动日志到底部
            const p = previews[currentTab];
            if (p) p.logs.scrollTop = p.logs.scrollHeight;
            if (currentTab === 'fill') loadDocumentTypes();
            else if (currentTab === 'record') loadPrerequisiteScripts();
        }
    });
});

// ========== 状态管理 ==========
let currentChatSession = null;

function setStatus(status, text, tab = null) {
    const p = previews[tab || currentTab];
    if (!p) return;
    p.statusBadge.className = 'status-badge ' + status;
    p.statusBadge.textContent = text;
    p.stopBtn.classList.toggle('hidden', status !== 'running');
}

function clearPreview(tab = null) {
    const p = previews[tab || currentTab];
    if (!p) return;
    p.logs.innerHTML = '<div class="log-entry"><span class="timestamp">[系统]</span> 等待开始...</div>';
    p.screenshot.src = DEFAULT_PLACEHOLDER;
    p.statusBadge.className = 'status-badge idle';
    p.statusBadge.textContent = '空闲';
    // record 预览无 resultText（死元素已删，J.5.3）；fill 保留，加守卫
    if (p.resultText) { p.resultText.textContent = ''; p.resultText.classList.add('hidden'); }
    p.stopBtn.classList.add('hidden');
    if ((tab || currentTab) === 'record') {
        document.getElementById('save-confirmation').classList.add('hidden');
        document.getElementById('interactive-help').classList.add('hidden');
    }
}

// #7：转义外部数据，防 innerHTML 注入（Playwright 报错含 <iframe> 被渲染成真元素的套娃 bug）。
// timestamp/stepTag 是安全 HTML 不转义；仅对 message/外部数据转义。raw=true 跳过（如截图 <img> 故意传 HTML）。
function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}

function addLog(message, tab = null, raw = false) {
    const p = previews[tab || currentTab];
    if (!p) return;
    const div = document.createElement('div');
    div.className = 'log-entry';
    const time = new Date().toLocaleTimeString();
    const safe = raw ? message : escapeHtml(message);
    div.innerHTML = `<span class="timestamp">[${time}]</span> ${safe}`;
    p.logs.appendChild(div);
    p.logs.scrollTop = p.logs.scrollHeight;
}

// ========== App Hub SignalR 事件（录制面板） ==========
appConnection.on("ReceiveLog", ({ taskId, message, stepName, timestamp }) => {
    if (taskId !== currentTaskId) return;
    const p = previews.record;
    const div = document.createElement('div');
    div.className = 'log-entry';
    const stepTag = stepName ? `<span class="step-tag">[${stepName}]</span> ` : '';
    div.innerHTML = `<span class="timestamp">[${new Date(timestamp).toLocaleTimeString()}]</span> ${stepTag}${escapeHtml(message)}`;  // #7：message 转义（timestamp/stepTag 安全 HTML 不转义）
    p.logs.appendChild(div);
    p.logs.scrollTop = p.logs.scrollHeight;
});

appConnection.on("ReceiveScreenshot", ({ taskId, image }) => {
    if (taskId !== currentTaskId) return;
    previews.record.screenshot.src = `data:image/png;base64,${image}`;
});

appConnection.on("RequestHelp", ({ taskId, question, image }) => {
    if (taskId !== currentTaskId) return;
    if (image) previews.record.screenshot.src = `data:image/png;base64,${image}`;
    addLog(`AI 请求帮助: ${question}`, 'record');
    setStatus('running', '等待用户输入', 'record');
    document.getElementById('help-question').textContent = question;
    document.getElementById('interactive-help').classList.remove('hidden');
});

appConnection.on("TaskCompleted", ({ taskId, result, script, errorMessage }) => {
    if (taskId !== currentTaskId) return;  // 顺带修既有 bug：过滤 taskId（与 ReceiveLog/RequestHelp/TaskStopped 一致），防多任务终态串扰
    if (currentTab === 'record') {
        // 录制终态（完成/取消/超时/失败）公共收尾：关闭请求帮助组件 + 清空输入框（三态都生效；原 TaskCompleted handler 漏关导致 request_help 挂着不关闭）
        document.getElementById('interactive-help').classList.add('hidden');
        const helpResponseInput = document.getElementById('help-response');
        if (helpResponseInput) helpResponseInput.value = '';
        const isSuccess = result === 'Success';
        const isCancelled = result === 'Cancelled';

        if (isSuccess) {
            setStatus('completed', '已完成', 'record');
            addLog('录制完成！', 'record');
        } else if (isCancelled) {
            setStatus('idle', '已取消', 'record');
            addLog(errorMessage ? `录制已取消: ${errorMessage}` : '录制已取消', 'record');
        } else {
            setStatus('failed', '执行失败', 'record');
            addLog(errorMessage ? `录制失败: ${errorMessage}` : '录制失败', 'record');
        }

        // 有步骤时弹出保存对话框（Success 或 Cancelled）
        if ((isSuccess || isCancelled) && script?.phases) {
            const countSteps = (items) => (items || []).reduce((n, it) => n + (it.kind === 'phase' ? countSteps(it.steps) : 1), 0);
            const stepCount = countSteps(script.phases);
            currentScriptData = script;
            if (stepCount > 0) {
                addLog(`共 ${stepCount} 个步骤`, 'record');
                document.getElementById('save-confirmation').classList.remove('hidden');
                const docTypeInput = document.getElementById('doc-type-input');
                const scriptNameInput = document.getElementById('script-name');
                if (docTypeInput && scriptNameInput)
                    scriptNameInput.value = docTypeInput.value + ' - ' + new Date().toLocaleDateString();
            }
        }

        const startBtn = document.getElementById('btn-start-record');
        if ((isStoppedByUser || isSuccess || isCancelled) && startBtn) {
            startBtn.disabled = false;
            startBtn.textContent = '开始录制';
            // 录制选择附件按钮联动恢复（与 startBtn 同恢复点，5 处之一）：录制完成/取消（含 stepCount=0 不弹保存框）时恢复，防永久禁用→选附件无效
            document.getElementById('btn-select-record-files').disabled = false;
            document.getElementById('record-file-input').disabled = false;
        }
        previews.record.stopBtn.classList.add('hidden');
    }
    // #46：删 fill 分支死代码（Worker workerHubUrl 必非空→appConnection TaskCompleted 的 fill 分支不可达；双 Hub 职责：App Hub 只管录制）
    // 录制成功/取消且有可保存脚本时，保留 currentTaskId 供保存使用
    // 在保存或放弃脚本时再清除
    if (currentTab !== 'record' || !currentScriptData) {
        currentTaskId = null;
    }
});

appConnection.on("TaskStopped", ({ taskId }) => {
    if (taskId !== currentTaskId) return;  // P3-8：过滤 taskId（原不过滤，串扰其他任务终态）
    const tab = currentTab;
    setStatus('idle', '已停止', tab);
    addLog('任务已被停止', tab);
    previews[tab].stopBtn.classList.add('hidden');

    if (tab === 'record') {
        const startBtn = document.getElementById('btn-start-record');
        if (startBtn) { startBtn.disabled = false; startBtn.textContent = '开始录制'; }
        // 录制选择附件按钮联动恢复（与 startBtn 同恢复点，5 处之一）：用户点停止时恢复
        document.getElementById('btn-select-record-files').disabled = false;
        document.getElementById('record-file-input').disabled = false;
    } else if (tab === 'fill') {
        const backBtn = document.getElementById('btn-back-to-selector');
        if (backBtn) backBtn.style.display = 'inline-block';
    }

    currentTaskId = null;
});

// 启动 App Hub
appConnection.start().then(() => addLog('已连接到服务器', 'record')).catch(err => addLog('连接失败: ' + err, 'record'));

// ========== 帮助响应 ==========
// 放法 X 块A（2026-07-14）：判 rest 是否用户粘了 HTML（与后端 C# UserPastedHtml 两份一致，前端 UX 拦截 + 后端权威防绕过）。
function userPastedHtml(rest) {
    if (!rest) return false;
    if (/^(\/\/|\/)/.test(rest) && !/</.test(rest)) return false;  // XPath 守卫（对齐后端 R8"不含 <"；//div<x 残缺< 判粘）
    if (/<\/?[a-zA-Z][a-zA-Z0-9]*\b[^>]*>/.test(rest)) return true;       // HTML 标签
    if (/[<>]/.test(rest)) return true;                                   // 残缺 < >
    return false;                                                          // 纯文字
}
document.getElementById('btn-send-help').addEventListener('click', async () => {
    const helpResponse = document.getElementById('help-response');
    const message = helpResponse.value.trim();
    if (!message) return;
    // b 必填校验（用户 2026-07-14 拍板不兜底）：选 (b) 无 HTML -> 前端拦截不提交（确定聚焦录入 / 取消清空）；后端 AppendHelpHtmlIfNeeded 防绕过兜底。
    // a-cached-hopper（方案 A，2026-07-16）：仅当当前求助是 BuildHelpQuestion 的 (b) AI 分析 HTML 菜单（selector 脆弱/多匹配/priority9 等）时拦截；
    // 字段重名/phase 重名的 (b)（手写菜单，语义=换名/新阶段，不需 HTML）放行透传 AI。判据：#help-question.textContent 含 '(b) AI 分析 HTML'
    // （等价 BuildHelpQuestion 菜单——BuildHelpQuestion 所有调用经 Common5 含 AnalyzeHtml→都含该字样，手写菜单全不含；空文本安全降级不拦）。
    const currentQuestion = document.getElementById('help-question').textContent ?? '';
    const m = message.match(/^b[)\.\s、：:，]?\s*(.*)$/is);
    if (m && currentQuestion.includes('(b) AI 分析 HTML') && !userPastedHtml(m[1])) {
        if (confirm('b 选项需要录入 HTML')) {
            helpResponse.focus();  // 确定：聚焦让用户补 HTML，不提交
        } else {
            helpResponse.value = '';  // 取消：清空放弃 b
        }
        return;  // 不提交
    }
    try {
        const res = await fetch('/api/record/respond', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ taskId: currentTaskId, response: message })
        });
        if (res.status === 409) {
            const err = await res.json();
            alert(err.error);
            document.getElementById('interactive-help').classList.add('hidden');
            return;
        }
        addLog(`已发送指令: ${message}`, 'record');
        document.getElementById('interactive-help').classList.add('hidden');
        helpResponse.value = '';
        setStatus('running', '继续执行中...', 'record');
    } catch (e) { alert('发送失败: ' + e.message); }
});

// ========== 录制功能 ==========
document.getElementById('btn-start-record').addEventListener('click', async () => {
    const docType = document.getElementById('doc-type-input').value.trim();
    const taskDesc = document.getElementById('task-desc').value.trim();
    clearPreview();
    if (!docType || !taskDesc) { alert('请填写单据类型和任务描述'); return; }

    // 录制选择附件按钮禁用：L258 uploadFiles 期间防 push 类竞态（D-Rec）+ 录制进行中再加文件不会上传（uploadFiles 已结束）；与 btn-start-record 同生命周期恢复
    document.getElementById('btn-select-record-files').disabled = true;
    document.getElementById('record-file-input').disabled = true;

    // 上传附件
    const fileInfos = await uploadFiles(recordFiles);

    try {
        setStatus('running', '录制中...', 'record');
        addLog(`开始录制: ${docType}`, 'record');
        isStoppedByUser = false;
        const res = await fetch('/api/record/start', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                documentTypeId: docType, taskDescription: taskDesc,
                username: document.getElementById('username').value,
                password: document.getElementById('password').value,
                prerequisiteScriptId: document.getElementById('prerequisite-script').value,
                attachments: fileInfos
            })
        });
        if (!res.ok) throw new Error(await res.text());
        const task = await res.json();
        currentTaskId = task.taskId;
        await appConnection.invoke("JoinTask", currentTaskId);
        addLog(`任务ID: ${currentTaskId}`, 'record');
        document.getElementById('btn-start-record').disabled = true;
        document.getElementById('btn-start-record').textContent = '录制中...';
        previews.record.stopBtn.classList.remove('hidden');
    } catch (e) {
        addLog('启动失败: ' + e.message, 'record');
        setStatus('idle', '空闲', 'record');
        document.getElementById('btn-start-record').disabled = false;
        document.getElementById('btn-start-record').textContent = '开始录制';
        // 录制选择附件按钮联动恢复（与 startBtn 同恢复点，5 处之一）：启动失败时恢复
        document.getElementById('btn-select-record-files').disabled = false;
        document.getElementById('record-file-input').disabled = false;
    }
});

// 保存成功收尾（首次保存 / forceSave 重发共用，避免重发成功后保存框不关、状态不一致）
function finishSave(scriptName, scriptId) {
    addLog(`脚本已保存: ${scriptName} (ID: ${scriptId})`, 'record');
    currentScriptData = null;
    currentTaskId = null;
    document.getElementById('save-confirmation').classList.add('hidden');
    setStatus('idle', '空闲', 'record');
    document.getElementById('btn-start-record').disabled = false;
    document.getElementById('btn-start-record').textContent = '开始录制';
    // 录制选择附件按钮联动恢复（与 startBtn 同恢复点，5 处之一）：保存成功时恢复
    document.getElementById('btn-select-record-files').disabled = false;
    document.getElementById('record-file-input').disabled = false;
    previews.record.stopBtn.classList.add('hidden');
    alert('脚本保存成功！');
}

// 保存脚本
document.getElementById('btn-save-script').addEventListener('click', async () => {
    const scriptName = document.getElementById('script-name').value.trim();
    if (!scriptName) { alert('请输入脚本名称'); return; }
    const saveBody = { taskId: currentTaskId, name: scriptName, script: currentScriptData };
    try {
        const res = await fetch('/api/record/save', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(saveBody)
        });
        if (!res.ok) {
            // 先读 body 为 text（Fetch body 流只能读一次，res.json() 后 res.text() 必 reject；先 text 再 JSON.parse，
            // 才能在非 JSON body 如中间件纯文本错误时拿到原始文本当 message——原 res.json()+res.text() 写法 text 回退是死代码）
            const rawText = await res.text().catch(() => '');
            let errBody = null;
            try { errBody = rawText ? JSON.parse(rawText) : null; } catch { errBody = null; }
            if (errBody && errBody.validationFailed) {
                // 校验失败（缺 aiGoal 等不完整脚本）：confirm 是否强制保存（不显示详细错误）→ forceSave=true 重发跳校验
                if (confirm('脚本存在错误，可能无法正常执行。是否仍要保存？')) {
                    const forceRes = await fetch('/api/record/save', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ ...saveBody, forceSave: true })
                    });
                    if (!forceRes.ok) {
                        // 重发失败（forceSave=true 不会再返 validationFailed，但可能有 IO/网络等其他错误）：直接告警 + 保持保存框开启（可重试/放弃），不再走 confirm（防 confirm-重发循环）
                        const forceErrText = await forceRes.text().catch(() => '未知错误');
                        addLog('强制保存失败: ' + forceErrText, 'record');
                        return;
                    }
                    const forceData = await forceRes.json();
                    finishSave(scriptName, forceData.scriptId);
                    return;
                }
                return;  // 用户取消强制保存，保持保存框开启
            }
            // 非 validationFailed 错误：用 errBody.error 或原始文本当 message（rawText 已在读 body 时取得）
            const msg = errBody && errBody.error ? errBody.error : (rawText || '未知错误');
            throw new Error(msg);
        }
        const data = await res.json();
        finishSave(scriptName, data.scriptId);
    } catch (e) { addLog('保存失败: ' + e.message, 'record'); }
});

// 放弃脚本
document.getElementById('btn-discard-script').addEventListener('click', () => {
    document.getElementById('save-confirmation').classList.add('hidden');
    currentScriptData = null;
    currentTaskId = null;
    setStatus('idle', '空闲', 'record');
    addLog('已放弃保存脚本', 'record');
    document.getElementById('btn-start-record').disabled = false;
    document.getElementById('btn-start-record').textContent = '开始录制';
    // 录制选择附件按钮联动恢复（与 startBtn 同恢复点，5 处之一）：放弃脚本时恢复
    document.getElementById('btn-select-record-files').disabled = false;
    document.getElementById('record-file-input').disabled = false;
    previews.record.stopBtn.classList.add('hidden');
});

// ========== 填报功能（对话收集 + Worker 执行） ==========

async function loadDocumentTypes() {
    const select = document.getElementById('fill-doc-type');
    select.innerHTML = '<option value="">加载中...</option>';
    try {
        const res = await fetch('/api/fill/doctypes');
        if (res.ok) {
            const types = await res.json();
            select.innerHTML = '<option value="">请选择单据类型</option>';
            const added = new Set();
            types.forEach(t => {
                if (!added.has(t.id)) {
                    const opt = document.createElement('option');
                    opt.value = t.id;
                    opt.textContent = t.name;
                    select.appendChild(opt);
                    added.add(t.id);
                }
            });
        }
    } catch { select.innerHTML = '<option value="">加载失败</option>'; }
}

async function loadPrerequisiteScripts() {
    const select = document.getElementById('prerequisite-script');
    select.innerHTML = '<option value="">(无) 直接开始</option>';
    try {
        const res = await fetch('/api/script/list');
        if (res.ok) {
            const scripts = await res.json();
            scripts.forEach(s => {
                const opt = document.createElement('option');
                opt.value = s.scriptId;
                // schema 补全后 hasErrors 反映 schema 或业务任一层校验失败（原决策"前置下拉不改"已修正为统一标⚠️——
                // 含未知字段/带病脚本都标⚠️提示，选了执行时 throw Failed 快速明确失败，不冲突）
                opt.textContent = s.hasErrors ? '⚠️ ' + s.name : s.name;
                select.appendChild(opt);
            });
        }
    } catch { }
}

// 开始对话
document.getElementById('btn-start-chat').addEventListener('click', async () => {
    const docTypeSelect = document.getElementById('fill-doc-type');
    const docTypeId = docTypeSelect.value.trim();
    const docTypeName = docTypeSelect.options[docTypeSelect.selectedIndex].text;
    if (!docTypeId) { alert('请选择单据类型'); return; }

    document.getElementById('doc-selector').classList.add('hidden');
    document.getElementById('chat-container').classList.remove('hidden');
    document.getElementById('chat-header-controls').classList.remove('hidden');

    currentChatSession = { docType: docTypeId, docTypeName, sessionId: null };
    chatHistory.innerHTML = '';
    clearPreview('fill');  // 清操作日志/截图/状态(原漏清#fill-logs，返回再开始对话残留上次日志)
    chatInput.disabled = false;
    btnSend.disabled = false;
    document.getElementById('btn-select-fill-files').disabled = false;  // 修复：重新开始对话时恢复附件按钮（L472 数据收集完成时禁用，原漏恢复→返回再进对话后按钮灰）
    document.getElementById('fill-file-input').disabled = false;  // 项1 联动恢复：sendChat isComplete 路径保留 fill-file-input 禁用，重启对话时恢复（防永久禁用→选附件无效）

    try {
        await callChatApi({
            scriptId: docTypeId, message: "用户已打开填报对话，请主动打招呼并引导用户填写。",
            sessionId: null, attachments: [],
            dataCollectionPrompt: document.getElementById('fill-data-prompt')?.value?.trim() || ''
        });
    } catch { addChatMessage('agent', '你好！我是填报助手。请告诉我需要填写的信息。'); }
    setStatus('idle', '空闲');
});

// 返回选择单据
document.getElementById('btn-back-to-selector').addEventListener('click', () => {
    if (isUploading) return;  // A③ 同源补漏（第1轮核查发现）：uploadFiles 期间禁止返回，防 clearPendingFillAttachments 清空 fillFiles → uploadFiles for...of 迭代器跳项 + UI 中断（与 removeFile 守卫同源，uploadFiles 期间 fillFiles 修改竞态；首次发送 sessionId=null 时 backBtn 不 confirm 直接执行，窗口虽窄但后果=部分附件丢失+对话中断）
    if (currentChatSession?.sessionId && !confirm('对话正在进行中，确定要返回吗？')) return;
    document.getElementById('chat-container').classList.add('hidden');
    document.getElementById('doc-selector').classList.remove('hidden');
    document.getElementById('chat-header-controls').classList.add('hidden');
    chatHistory.innerHTML = '';
    clearPreview('fill');  // 清操作日志/截图/状态(原漏清#fill-logs)
    currentChatSession = null;
    chatInput.disabled = true;
    btnSend.disabled = true;
    document.getElementById('btn-select-fill-files').disabled = false;  // 返回时同步恢复附件按钮（双保险，保持状态一致）
    document.getElementById('fill-file-input').disabled = false;  // 项1 联动恢复：返回选择单据时恢复 fill-file-input（与 btn-select-fill-files 同步，防永久禁用）
    clearPendingFillAttachments();  // 复用：清空待发附件（sendChat 发送成功后同款清理）
    document.getElementById('chat-status-text').textContent = '对话进行中';
    previews.fill.resultText.textContent = '';
    previews.fill.resultText.classList.add('hidden');
});

function addChatMessage(role, content, attachments) {
    const div = document.createElement('div');
    div.className = 'message ' + role;
    // 文本进子元素：不能用 div.textContent（会清掉后续 appendChild 的附件块）
    if (content) {
        const textEl = document.createElement('div');
        textEl.textContent = content;
        div.appendChild(textEl);
    }
    // 附件 chip 保留在对话记录里（附件随消息发送后，待发列表清空但历史保留）
    if (attachments && attachments.length) {
        const attEl = document.createElement('div');
        attEl.style.display = 'flex';
        attEl.style.flexDirection = 'column';
        attEl.style.gap = '6px';
        if (content) attEl.style.marginTop = '8px';
        attachments.forEach(a => {
            const chip = document.createElement('div');
            chip.className = 'file-item';
            const sizeText = a.size ? ` (${(a.size / 1024).toFixed(1)} KB)` : '';
            chip.innerHTML = `<div class="file-item-info"><span class="file-item-icon">📎</span><span class="file-item-name">${escapeHtml(a.name || '')}${sizeText}</span></div>`;
            attEl.appendChild(chip);
        });
        div.appendChild(attEl);
    }
    chatHistory.appendChild(div);
    chatHistory.scrollTop = chatHistory.scrollHeight;
    return div;  // 项2：返回 div 引用，供 sendChat 失败时精确移除 user 气泡（替代 chatHistory.lastChild 臆断）
}

// 清空填报待发附件列表（sendChat 发送成功后 / 返回选择单据时复用）
function clearPendingFillAttachments() {
    fillFiles.length = 0;
    const listEl = document.getElementById('fill-file-list');
    if (listEl) listEl.innerHTML = '';
    const inputEl = document.getElementById('fill-file-input');
    if (inputEl) inputEl.value = '';  // 清 input.value，允许重选同一文件
    const countEl = document.getElementById('fill-file-count');
    if (countEl) countEl.textContent = '未选择文件';
}

async function callChatApi(requestBody) {
    const res = await fetch('/api/fill/chat', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(requestBody)
    });
    if (!res.ok) {
        let msg = '请求失败';
        try { msg = (await res.json()).error || msg; } catch {}
        addChatMessage('agent', '⚠️ ' + msg);
        return { success: false, error: msg };
    }
    const data = await res.json();
    currentChatSession.sessionId = data.sessionId;
    if (data.reply) addChatMessage('agent', data.reply);

    // 数据收集完成，显示执行按钮
    if (data.isComplete && data.collectedData) {
        addLog('信息收集完成', 'fill');
        document.getElementById('chat-status-text').textContent = '信息收集完成';
        chatInput.disabled = true;
        btnSend.disabled = true;
        document.getElementById('btn-select-fill-files').disabled = true;

        const actionDiv = document.createElement('div');
        actionDiv.className = 'chat-actions';
        actionDiv.style.textAlign = 'center';
        actionDiv.style.marginTop = '10px';
        const btnExec = document.createElement('button');
        btnExec.className = 'primary';
        btnExec.textContent = '开始执行填报';
        btnExec.onclick = async () => {
            btnExec.disabled = true;
            btnExec.textContent = '填报中...';
            try { await startDynamicFillTask(data.collectedData, data.reply); }
            catch (error) { alert('启动失败: ' + error.message); btnExec.disabled = false; btnExec.textContent = '开始执行填报'; }
        };
        actionDiv.appendChild(btnExec);
        chatHistory.appendChild(actionDiv);
        chatHistory.scrollTop = chatHistory.scrollHeight;
    }
    return { success: true, data };
}

let isSending = false;  // 防重入：uploadFiles/await 期间禁止再次点发送
let isUploading = false;  // uploadFiles 进行中标志：守卫 removeFile 防迭代器竞态（A③，覆盖 fillFiles+recordFiles 两类调用）
async function sendChat() {
    if (isSending) return;
    const message = chatInput.value.trim();
    if (!message && fillFiles.length === 0) return;  // 空消息+无附件才不发（允许仅附件或仅文字）

    // 先渲染用户气泡：附件名立即可用（item.file.name），不等待上传；项2 拿 div 引用（O1 失败精确移除）
    const pendingAttachments = fillFiles.map(item => ({ name: item.file.name, size: item.file.size }));
    const userBubble = addChatMessage('user', message, pendingAttachments);
    chatInput.value = '';

    isSending = true;
    // 项1+8 守卫：用本地变量承载 isComplete（不能用 chatInput.disabled——项8 自身会置 true 污染信号，finally 恒判已禁用 → 输入区永久锁死）
    let chatCompleted = false;
    const btnSelectFiles = document.getElementById('btn-select-fill-files');
    const fillFileInput = document.getElementById('fill-file-input');
    try {
        // 项1+8：上传期间锁定整个输入区（附件选择 + 文本输入），防竞态；finally 据本地变量恢复
        chatInput.disabled = true;
        btnSelectFiles.disabled = true;
        fillFileInput.disabled = true;
        // 发送时统一上传附件拿 path/url（仿录制 uploadFiles(recordFiles)），注入对话让 AI 感知
        const uploaded = await uploadFiles(fillFiles);
        // O1 守卫（silent-success 防御）：附件全失败 → 移除刚渲染的 user 气泡（项2 精确移除，不依赖 lastChild），提示，保留 fillFiles 让用户重试
        if (fillFiles.length > 0 && uploaded.length === 0) {
            userBubble.remove();
            addChatMessage('agent', '⚠️ 附件全部上传失败，请检查后重新发送。');
            chatInput.value = message;  // 项3：O1 全失败还原文字
            return;
        }
        // Bug-2（可观测）：部分附件失败时 chat 面板提示（uploadFiles 失败日志在 fill 面板，chat 面板易漏看）
        if (uploaded.length < fillFiles.length) {
            addChatMessage('agent', `⚠️ ${fillFiles.length - uploaded.length} 个附件上传失败，AI 仅收到其中 ${uploaded.length} 个。`);
        }
        const result = await callChatApi({
            scriptId: currentChatSession.docType, message, sessionId: currentChatSession.sessionId,
            attachments: uploaded, dataCollectionPrompt: document.getElementById('fill-data-prompt')?.value?.trim() || ''
        });
        // 项1+8：抓 isComplete（callChatApi 数据收集完成时禁用 chatInput/btnSend/btn-select-fill-files，finally 跳过恢复保留其禁用）
        if (result?.data?.isComplete) chatCompleted = true;
        // Bug-1（错误路径一致性）：仅发送成功才清空待发附件；项3 路径③：HTTP 错误（success:false）还原文字（callChatApi 已 addChatMessage 错误，不重复提示）
        if (result && result.success) clearPendingFillAttachments();
        else chatInput.value = message;
    } catch (e) {
        addChatMessage('agent', '抱歉，发生错误：' + e.message);
        chatInput.value = message;  // 项3：网络错误也还原文字（与 O1 一致）
    } finally {
        // 项1+8：未完成才恢复（callChatApi isComplete 时保留其禁用；fill-file-input callChatApi 未动，跳过恢复保留项1 禁用，行为一致）
        if (!chatCompleted) {
            chatInput.disabled = false;
            btnSelectFiles.disabled = false;
            fillFileInput.disabled = false;
        }
        isSending = false;
    }
}

btnSend.addEventListener('click', sendChat);
chatInput.addEventListener('keypress', (e) => { if (e.key === 'Enter') sendChat(); });

// ========== v2: 填报任务启动（全部走 Worker 远程执行） ==========
async function startDynamicFillTask(collectedData, summary) {
    document.getElementById('chat-status-text').textContent = '正在执行填报...';
    const backBtn = document.getElementById('btn-back-to-selector');
    if (backBtn) backBtn.style.display = 'none';

    const docType = currentChatSession.docType;
    // 附件不经 start-dynamic 传递：后端 StartFillRequest 无 Attachments 字段（被忽略冗余）；
    // file 字段附件由 AI 对话收集期写入 data[fileField] → collectedData → fillData 流转。
    // 且 sendChat 发送成功后 fillFiles 已清空，此处亦恒空。

    clearPreview();
    setStatus('running', '正在启动填报...', 'fill');
    addLog(`开始执行填报任务: ${docType}`, 'fill');

    // #8：显示预填充数据（参考 v1，让用户看到本次填的是什么）。
    // 时序优势：此刻 collectedData 尚未含 username/password（下方 fillUsername/fillPassword 才加），天然避开密码。
    // 复合类型（对象/数组如 attachments）JSON.stringify，避免 [object Object]。
    // 按脱敏总原则（日志不脱敏）：用户自填数据前端可见用于诊断，明文显示（去掉原计划 sensitive 脱敏）。
    if (collectedData && Object.keys(collectedData).length > 0) {
        addLog('────────────────────────────', 'fill');
        addLog(`📊 预填充数据 (${Object.keys(collectedData).length} 项):`, 'fill');
        for (const [key, value] of Object.entries(collectedData)) {
            const display = (value !== null && typeof value === 'object') ? JSON.stringify(value) : value;
            addLog(`   • ${key}: ${display}`, 'fill');
        }
        addLog('────────────────────────────', 'fill');
    }

    const fillUsername = document.getElementById('fill-username').value;
    const fillPassword = document.getElementById('fill-password').value;
    if (fillUsername) collectedData['username'] = fillUsername;
    if (fillPassword) collectedData['password'] = fillPassword;

    try {
        const res = await fetch('/api/fill/start-dynamic', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                documentTypeId: docType,
                fillData: collectedData
            })
        });
        if (!res.ok) {
            const errData = await res.json().catch(() => ({}));
            throw new Error(`服务返回错误: ${res.status} ${errData.error || res.statusText}`);
        }

        const task = await res.json();
        currentTaskId = task.taskId;
        if (!currentTaskId) throw new Error('任务ID为空');
        addLog(`填报任务 ID: ${currentTaskId}`, 'fill');

        // v2 双 Hub：连接 Worker Hub 接收填报进度
        const workerHubUrl = task.workerHubUrl;
        if (workerHubUrl) {
            addLog(`正在连接 Worker 实时通信...`, 'fill');
            workerConnection = new signalR.HubConnectionBuilder()
                // 跨域连 Worker Hub：Worker 用 AllowAnyOrigin（"["*"]" 不校验，回 *），而 signalr 浏览器端默认 withCredentials=true，
                // 会触发“带 credentials 时 origin 不能为 *”被浏览器拦截（Failed to fetch）。本项目 Worker 匿名、无 cookie 依赖，显式关闭即可
                .withUrl(workerHubUrl, { withCredentials: false })
                .withAutomaticReconnect()
                .build();

            workerConnection.on("ReceiveLog", ({ taskId, message, stepName, timestamp }) => {
                if (taskId !== currentTaskId) return;
                const p = previews.fill;
                const div = document.createElement('div');
                div.className = 'log-entry';
                const stepTag = stepName ? `<span class="step-tag">[${stepName}]</span> ` : '';
                div.innerHTML = `<span class="timestamp">[${new Date(timestamp).toLocaleTimeString()}]</span> ${stepTag}${escapeHtml(message)}`;  // #7：message 转义
                p.logs.appendChild(div);
                p.logs.scrollTop = p.logs.scrollHeight;
            });

            workerConnection.on("ReceiveScreenshot", ({ taskId, image }) => {
                if (taskId !== currentTaskId) return;
                previews.fill.screenshot.src = `data:image/png;base64,${image}`;
            });

            workerConnection.on("TaskCompleted", ({ taskId, result, resultData, screenshot, errorMessage, failureType }) => {
                if (taskId !== currentTaskId) return;
                handleFillCompleted(result, resultData, screenshot, errorMessage, failureType);
                workerConnection?.stop();
            });

            workerConnection.on("TaskStopped", ({ taskId }) => {
                if (taskId !== currentTaskId) return;
                addLog('任务已被停止', 'fill');
                // P3-8：停止→idle/已停止，buttonMode=retry（恢复"开始执行填报"，对话数据保留可微调后重试）
                finalizeFillState({ status: 'idle', statusText: '已停止', chatStatusText: '已停止', buttonMode: 'retry' });
                workerConnection?.stop();
            });

            // R1-10：重连反馈——withAutomaticReconnect 下静默自动重连（默认最长 42s）变可见。
            // onclose 不动（仅在重连彻底失败后触发，"连接中断"语义仍正确）；currentTaskId 不清（残留下次覆盖无害）
            let reconnectAttempt = 0;
            workerConnection.onreconnecting((error) => {
                if (currentTaskId && currentTab === 'fill') {
                    reconnectAttempt++;
                    setStatus('running', `正在重连…第 ${reconnectAttempt} 次`, 'fill');
                    addLog(`与 Worker 连接断开，正在重连（第 ${reconnectAttempt} 次）…`, 'fill');
                }
            });
            workerConnection.onreconnected((connectionId) => {
                if (currentTaskId && currentTab === 'fill') {
                    reconnectAttempt = 0;
                    setStatus('running', '已重连', 'fill');
                    addLog('与 Worker 重新连接成功', 'fill');
                }
            });

            workerConnection.onclose((error) => {
                if (currentTaskId && currentTab === 'fill') {
                    const stopBtn = previews.fill.stopBtn;
                    // P3-8：保留守卫——正常完成 finalizeFillState 先隐藏 stopBtn 再 stop()，onclose 守卫 false 不误报
                    if (!stopBtn.classList.contains('hidden')) {
                        addLog('与 Worker 的连接断开（Worker 可能崩溃或网络中断）。请尝试刷新页面或返回重试', 'fill');
                        finalizeFillState({ status: 'failed', statusText: '连接中断', chatStatusText: '连接中断', buttonMode: 'retry' });
                    }
                }
            });

            await workerConnection.start();
            await workerConnection.invoke("JoinTask", currentTaskId);
            addLog(`已连接到 Worker 实时通信`, 'fill');
        } else {
            // #46：Worker 未返回实时通信地址（workerHubUrl 必非空，此分支理论不可达）；显式报错而非静默 fallback 到残缺路径（无 resultData 的 App Hub 兜底）
            setStatus('failed', '无法接收填报进度', 'fill');
            addLog('Worker 未返回实时通信地址，无法接收填报进度。请检查 Worker 配置或刷新重试', 'fill');
            return;
        }

        previews.fill.stopBtn.classList.remove('hidden');
    } catch (e) {
        addLog('启动失败: ' + e.message, 'fill');
        if (workerConnection) { try { await workerConnection.stop(); } catch { } }
        // 与 onclose（连接中断）/ handleFillCompleted（执行失败）同构：启动层失败也走公共收尾，
        // 恢复"开始执行填报"按钮(buttonMode:'retry')+ 状态文字 + 返回按钮 + 隐藏 stopBtn，避免只能刷新恢复。
        finalizeFillState({ status: 'failed', statusText: '启动失败', chatStatusText: '启动失败', buttonMode: 'retry' });
    }
}

// P3-8：填报终态公共收尾（4 终态入口统一：成功/失败/停止/中断）。
// buttonMode: 'retry'=恢复"开始执行填报"（对话数据保留可微调后重试，失败/停止/中断用）/ 'newform'=变"📝 填写新单据"（成功用）。
function finalizeFillState({ status, statusText, chatStatusText, buttonMode, showBack = true }) {
    setStatus(status, statusText, 'fill');
    previews.fill.stopBtn.classList.add('hidden');
    document.getElementById('chat-status-text').textContent = chatStatusText;
    if (buttonMode === 'retry') restoreExecButton();
    else if (buttonMode === 'newform') updateExecButtonToNewForm();
    if (showBack) { const backBtn = document.getElementById('btn-back-to-selector'); if (backBtn) backBtn.style.display = 'inline-block'; }
}

function handleFillCompleted(result, resultData, screenshot, errorMessage, failureType) {
    const isSuccess = result === 'Success';
    if (isSuccess) {
        // #45 决策10：resultData 是 returnData 字典（多键），遍历展示所有返回字段；billCode 兼容首键保留
        const entries = resultData && typeof resultData === 'object'
            ? Object.entries(resultData).filter(([, v]) => v != null && v !== '')
            : [];
        if (entries.length > 0) {
            addLog(`填报已完成：${entries.map(([k, v]) => `${k}=${v}`).join('，')}`, 'fill');
            previews.fill.resultText.textContent = entries.map(([k, v]) => `${k}：${v}`).join('  ');
            previews.fill.resultText.classList.remove('hidden');
        } else {
            addLog('填报已完成', 'fill');
        }
        finalizeFillState({ status: 'completed', statusText: '已完成', chatStatusText: '填报已完成', buttonMode: 'retry' });
    } else {
        const reason = errorMessage || result || '未知错误';
        const tag = failureType ? `[${failureType}]` : '';
        addLog(`填报失败${tag}: ${reason}`, 'fill');
        finalizeFillState({ status: 'failed', statusText: '执行失败', chatStatusText: '填报失败', buttonMode: 'retry' });
    }
    if (screenshot) addLog(`<img src="data:image/png;base64,${screenshot}" style="max-width:100%;border-radius:8px;margin-top:4px;" />`, 'fill', true);  // #7 TD-1：raw=true 跳过转义（截图故意传 <img>）
}

// 填报成功：将"开始执行填报/填报中..."按钮变为"📝 填写新单据"（对齐 v1，避免完成后按钮卡在"填报中..."）
function updateExecButtonToNewForm() {
    chatHistory.querySelectorAll('.chat-actions button').forEach(btn => {
        if (btn.textContent === '开始执行填报' || btn.textContent === '填报中...') {
            btn.textContent = '📝 填写新单据';
            btn.disabled = false;
            btn.onclick = () => document.getElementById('btn-back-to-selector').click();
        }
    });
}

// 填报失败：恢复"开始执行填报"按钮，允许重试
function restoreExecButton() {
    chatHistory.querySelectorAll('.chat-actions button').forEach(btn => {
        if (btn.textContent === '填报中...') {
            btn.disabled = false;
            btn.textContent = '开始执行填报';
        }
    });
}

// ========== 停止按钮 ==========
async function stopTask(tab) {
    if (!currentTaskId) { addLog('没有正在运行的任务', tab); return; }
    isStoppedByUser = true;
    try {
        if (tab === 'record')
            await fetch('/api/record/stop', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ taskId: currentTaskId }) });
        else
            await fetch(`/api/fill/stop/${currentTaskId}`, { method: 'POST' });
        addLog('已发送停止请求', tab);
    } catch (e) { addLog('停止失败: ' + e.message, tab); }
}

document.getElementById('btn-stop-record').addEventListener('click', () => stopTask('record'));
document.getElementById('btn-stop-fill').addEventListener('click', () => stopTask('fill'));

// ========== 单据管理 ==========
async function loadDocManagerList() {
    const listEl = document.getElementById('doc-list');
    listEl.innerHTML = '';
    try {
        const res = await fetch('/api/document');
        if (!res.ok) return;
        const docs = await res.json();
        docs.forEach(doc => {
            const card = document.createElement('div');
            card.className = 'doc-card';
            card.innerHTML = `
                <div class="doc-card-header">
                    <span class="doc-card-icon">${escapeHtml(doc.icon || '📄')}</span>
                    <span class="doc-card-title">${escapeHtml(doc.name)}</span>
                </div>
                <div class="doc-card-meta">${escapeHtml(doc.description || '无描述')} | 脚本数: ${doc.orderedScriptIds?.length || 0}</div>
                <div class="doc-card-footer">
                    <button class="secondary" data-doc-action="edit">编辑</button>
                    <button class="danger" style="width:auto;padding:4px 12px;font-size:12px;margin-top:0" data-doc-action="delete">删除</button>
                </div>`;  // TD-P4：doc.id 不再注入 onclick 字符串（XSS 防御）；name/description/icon 已 escapeHtml（#7 TD-2；icon 第二轮核查补——与 doc.name 同表达式不对称，同 TD-P9 同型）
            // TD-P4：addEventListener 闭包传 doc.id（doc.id 不进 HTML 字符串，消除 onclick JS 逃逸风险）
            card.querySelector('[data-doc-action="edit"]')?.addEventListener('click', () => editDoc(doc.id));
            card.querySelector('[data-doc-action="delete"]')?.addEventListener('click', () => deleteDoc(doc.id));
            listEl.appendChild(card);
        });
        if (docs.length === 0) listEl.innerHTML = '<p style="color:var(--text-muted);text-align:center;padding:40px;">暂无单据类型，请点击"新建单据"创建</p>';
    } catch { }
}

let editingDocId = null;
document.getElementById('btn-create-doc').addEventListener('click', () => {
    editingDocId = null;
    document.getElementById('modal-title').textContent = '新建单据';
    document.getElementById('doc-name-input').value = '';
    document.getElementById('doc-data-processing-prompts').value = '';
    loadScriptCheckboxList([]);
    document.getElementById('doc-modal').classList.remove('hidden');
});

window.editDoc = async function(id) {
    editingDocId = id;
    const res = await fetch(`/api/document/${id}`);
    if (!res.ok) return;
    const doc = await res.json();
    document.getElementById('modal-title').textContent = '编辑单据';
    document.getElementById('doc-name-input').value = doc.name;
    document.getElementById('doc-data-processing-prompts').value = doc.dataProcessingPrompts || '';
    await loadScriptCheckboxList(doc.orderedScriptIds || []);
    document.getElementById('doc-modal').classList.remove('hidden');
};

window.closeDocModal = function() { document.getElementById('doc-modal').classList.add('hidden'); };

window.deleteDoc = async function(id) {
    if (!confirm('确定删除此单据类型？')) return;
    await fetch(`/api/document/${id}`, { method: 'DELETE' });
    loadDocManagerList();
};

async function loadScriptCheckboxList(selectedIds) {
    const container = document.getElementById('script-checkbox-list');
    container.innerHTML = '';
    try {
        const res = await fetch('/api/script/list');
        if (!res.ok) return;
        const scripts = await res.json();
        if (scripts.length === 0) { container.innerHTML = '<span style="color:var(--text-muted);font-size:13px;">暂无脚本，请先录制</span>'; return; }
        scripts.forEach(s => {
            const item = document.createElement('label');
            item.className = 'checkbox-item';
            // 带病脚本（业务校验失败的强制保存脚本）名前加 ⚠️ 提醒用户（hasErrors 来自 GetAllScripts 的 ValidateAndGetErrors）
            const displayName = s.hasErrors ? `⚠️ ${s.name}` : s.name;
            item.innerHTML = `<input type="checkbox" value="${escapeHtml(s.scriptId)}" ${selectedIds.includes(s.scriptId) ? 'checked' : ''}><span>${escapeHtml(displayName)}</span>`;  // #7 TD-2：displayName 转义；TD-P9：scriptId 进 value 属性也 escapeHtml（与 displayName 对称，防御未来 scriptId 生成逻辑变更）
            container.appendChild(item);
        });
    } catch { }
}

document.getElementById('btn-modal-save').addEventListener('click', async () => {
    const name = document.getElementById('doc-name-input').value.trim();
    if (!name) { alert('请输入单据名称'); return; }
    const prompts = document.getElementById('doc-data-processing-prompts').value.trim();
    const checkboxes = document.querySelectorAll('#script-checkbox-list input[type="checkbox"]:checked');
    const scriptIds = Array.from(checkboxes).map(cb => cb.value);

    const body = { name, description: '', dataProcessingPrompts: prompts, orderedScriptIds: scriptIds };
    if (editingDocId) {
        body.id = editingDocId;
        await fetch(`/api/document/${editingDocId}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    } else {
        await fetch('/api/document', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    }
    document.getElementById('doc-modal').classList.add('hidden');
    loadDocManagerList();
});

document.getElementById('btn-modal-cancel').addEventListener('click', closeDocModal);

// ========== 文件上传辅助 ==========
async function uploadFiles(fileArray) {
    const results = [];
    isUploading = true;  // A③：uploadFiles 进行中标志（try/finally 设/清，异常也清避免永久锁死；覆盖 sendChat L549 填报 + btn-start-record L258 录制两个调用点）
    try {
        for (const item of fileArray) {
            const formData = new FormData();
            formData.append('file', item.file);
            try {
                const res = await fetch('/api/upload', { method: 'POST', body: formData });
                if (res.ok) {
                    const info = await res.json();
                    results.push({ id: info.id, name: item.file.name, size: item.file.size, path: info.path, url: info.url });
                } else {
                    // 可观测（silent-success 防御）：上传失败（类型/大小/服务端拒）不静默吞，暴露给用户
                    addLog(`附件上传失败: ${item.file.name} (HTTP ${res.status})`, currentTab);
                }
            } catch (e) {
                addLog(`附件上传失败: ${item.file.name} (${e.message})`, currentTab);
            }
        }
    } finally {
        isUploading = false;
    }
    return results;
}

// 录制文件选择
document.getElementById('record-file-input').addEventListener('change', (e) => {
    handleFileSelect(e.target.files, recordFiles, 'record-file-list', 'record-file-count');
    e.target.value = '';  // 清value，允许重选同一文件(同fill-file-input)
});
document.getElementById('fill-file-input').addEventListener('change', (e) => {
    handleFileSelect(e.target.files, fillFiles, 'fill-file-list', 'fill-file-count');
    e.target.value = '';  // 清value，允许重选同一文件(否则选同一文件change不触发，附件不添加)
});

function handleFileSelect(files, storage, listId, countId) {
    Array.from(files).forEach(file => {
        const id = Math.random().toString(36).substring(2, 8);
        storage.push({ file, id });
    });
    const listEl = document.getElementById(listId);
    listEl.innerHTML = '';
    storage.forEach((item, idx) => {
        const div = document.createElement('div');
        div.className = 'file-item';
        div.innerHTML = `<span>${escapeHtml(item.file.name)} (${(item.file.size / 1024).toFixed(1)} KB)</span>
            <button class="file-item-remove" onclick="removeFile('${listId}', ${idx}, '${countId}')">×</button>`;
        listEl.appendChild(div);
    });
    document.getElementById(countId).textContent = storage.length > 0 ? `已选择 ${storage.length} 个文件` : '未选择文件';
}

window.removeFile = function(listId, index, countId) {
    if (isUploading) return;  // A③：uploadFiles 期间禁止 splice（防修改正在 for...of 迭代的数组→迭代器跳项 silent 不上传某附件；双覆盖 fillFiles+recordFiles）
    const storage = listId === 'record-file-list' ? recordFiles : fillFiles;
    storage.splice(index, 1);
    handleFileSelect([], storage, listId, countId);
    // 重新渲染
    const listEl = document.getElementById(listId);
    listEl.innerHTML = '';
    storage.forEach((item, idx) => {
        const div = document.createElement('div');
        div.className = 'file-item';
        div.innerHTML = `<span>${escapeHtml(item.file.name)} (${(item.file.size / 1024).toFixed(1)} KB)</span>
            <button class="file-item-remove" onclick="removeFile('${listId}', ${idx}, '${countId}')">×</button>`;
        listEl.appendChild(div);
    });
    document.getElementById(countId).textContent = storage.length > 0 ? `已选择 ${storage.length} 个文件` : '未选择文件';
};
