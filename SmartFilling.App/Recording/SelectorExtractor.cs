using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace SmartFilling.App.Recording;

/// <summary>
/// 从 DOM 元素提取最优 selector（7 阶段，纯 XPath）。
/// 阶段一：自身属性精确
/// 阶段二：自身属性组合
/// 阶段三：祖先路径（属性锚点 + 文本锚点）
/// 阶段四：近邻锚点（属性锚点 + 文本锚点）
/// 阶段五：属性包含匹配（合并单+多）
/// 阶段六：XPath 完整路径
/// 阶段七：AI 兜底
/// </summary>
public class SelectorExtractor
{
    public record SelectorResult(string? Selector, int Priority, List<string>? Candidates = null, bool NeedsAiAction = false, bool NeedsFallback = false);

    #region 常量

    /// <summary>祖先遍历：向上查找有特征祖先的最大层数</summary>
    private const int MaxAncestorDepth = 30;
    /// <summary>近邻锚点：每个方向检查的兄弟元素数量</summary>
    private const int MaxNearbySiblings = 10;
    /// <summary>近邻锚点：向上跨越几层父级搜索（1=只看兄弟，2=看叔伯）</summary>
    private const int MaxNearbyAncestorLevels = 2;
    /// <summary>近邻锚点：每层父级向上后，每方向检查的叔伯元素数量</summary>
    private const int MaxNearbyAncestorSiblings = 5;

    #endregion

    #region 动态值检测规则

    // §8 通用化：从"按见过的样例硬编码前缀（Z_/ext-gen/…）"重构为"按动态信号通用识别"（GUID 片段去锚+大小写 / 长数字 / 框架前缀清单去 Z_）。
    // 覆盖 zgy_iframe_<GUID>（带前缀+大写 GUID）误判为稳定的隐患——原整串小写 GUID 正则 ^...$ 漏判，BuildFrameSelectorAsync 会生成 GUID selector 回放失效。
    private static readonly (Regex pattern, string description)[] DynamicPatterns =
    [
        (new Regex(@"[a-fA-F0-9]{8}(-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12}", RegexOptions.Compiled), "GUID片段（去锚+大小写，覆盖纯/带前后缀）"),
        (new Regex(@"\d{6,}", RegexOptions.Compiled), "长数字（6位+，覆盖任意前缀时间戳/大序号，去Z_依赖）"),
        (new Regex(@"^(ext-gen|layui-layer|rc-[\w]+-)\d+$", RegexOptions.Compiled), "框架前缀清单（4条合并去Z_，短序号兜底）"),
        (new Regex(@"^\d+$", RegexOptions.Compiled), "纯数字"),
        (new Regex(@"^:r[a-z0-9]*:$", RegexOptions.Compiled), "React生成（[a-z0-9]覆盖React18 :r1:）"),
    ];

    private static readonly Regex CssHashSuffix = new(@"_[a-fA-F0-9]{5,}$", RegexOptions.Compiled);  // §8：加大写
    private static readonly Regex UrlHasQuery = new(@"\?.+=", RegexOptions.Compiled);

    /// <summary>
    /// 判断属性值是否为动态生成的（不稳定）
    /// </summary>
    public static bool IsStableValue(string value, string attributeName)
    {
        if (string.IsNullOrEmpty(value)) return false;

        // 通用动态模式检测
        foreach (var (pattern, _) in DynamicPatterns)
            if (pattern.IsMatch(value)) return false;

        // class 属性：检测 CSS hash 后缀
        if (attributeName == "class" && CssHashSuffix.IsMatch(value)) return false;

        // URL 类属性：检测查询参数
        if ((attributeName == "src" || attributeName == "href") && UrlHasQuery.IsMatch(value)) return false;

        return true;
    }

    /// <summary>
    /// 判断元素的 value 属性是否为「用户输入的业务数据」（会随回放改变）——这类 value 不能作为稳定选择器锚点。
    /// 可输入类（textarea/select/文本类 input）返回 true；按钮/选项类 input（submit/button/checkbox/radio 等）返回 false。
    /// </summary>
    private static bool IsValueUserData(string tag, string? type)
    {
        if (tag == "textarea" || tag == "select") return true;
        if (tag == "input")
        {
            var t = (type ?? string.Empty).Trim().ToLowerInvariant();
            // 按钮/选项类的 value 是固定的（按钮文字/选项标识），稳定；其余（含默认 text）为用户填入的业务数据
            return t is not ("button" or "submit" or "reset" or "image" or "checkbox" or "radio");
        }
        return false;
    }

    /// <summary>
    /// 尝试从属性值中去掉动态部分，返回稳定的包含匹配 selector
    /// </summary>
    public static string? TryExtractStablePart(string value, string attributeName)
    {
        if (string.IsNullOrEmpty(value)) return null;

        // 通用动态模式：尝试去掉尾部数字
        foreach (var (pattern, desc) in DynamicPatterns)
        {
            if (pattern.IsMatch(value))
            {
                // Z_1775616840 → 去掉数字部分 → Z_ （太短无意义）
                // ext-gen1234 → 去掉数字 → ext-gen （可能有用）
                var stable = pattern.Replace(value, "");
                if (stable.Length >= 2)
                    return stable;
                return null;
            }
        }

        // CSS hash 后缀：btn_1a2b3c → btn
        if (CssHashSuffix.IsMatch(value))
        {
            var m = CssHashSuffix.Match(value);
            var stable = value[..m.Index];
            if (stable.Length >= 2)
                return stable;
        }

        // URL 查询参数：取路径部分
        if ((attributeName == "src" || attributeName == "href") && UrlHasQuery.IsMatch(value))
        {
            var queryIdx = value.IndexOf('?');
            if (queryIdx > 0)
                return value[..queryIdx];
        }

        return null;
    }

    /// <summary>
    /// B6（2c 阶段）：分析 selector 字符串本身的脆弱性（不重新提取元素、不调 Playwright），粗分级 1-9。
    /// 复用 <see cref="IsStableValue"/> 判动态值（GUID/长数字/框架前缀）：解析 selector 里的精确属性锚点
    /// `@attr='val'`，对每个 val 调 IsStableValue——稳定则该锚点有效，不稳定（动态值）则该锚点失效降级。
    /// 用于"非 ref 入口"（save_step AI 直接传 selector / detect / captcha / fallback）的录制期脆弱度评估，
    /// 与 ref 入口 ExtractAsync（构造时精确 priority）对称——避免 AI 手写或直接传入的脆弱 selector silent 落盘。
    ///
    /// 粗分级（与 ExtractAsync priority 数值对齐，便于复用门禁 priority&gt;=8 求助 / &gt;=7 onError）：
    /// 1 id / 2 name / 3 data-* 精确（稳定值）→ 稳定；4 normalize-space(.) 精确文本；5 其他稳定属性精确 / class 精确；
    /// 7 contains（属性/文本包含，NeedsFallback）；8 纯位置路径/无稳定锚点/仅动态值锚点（高脆弱）；9 空/解析失败。
    /// 组合 selector（多锚点 and）取最优锚点分级——组合是为唯一，AssessFragility 评估锚点质量（最优锚点决定脆弱度）。
    /// 风险：CSS selector 无法解析 XPath 谓词，保守判 7（onError 兜底，不求助）；粗分级降歧义（不精确反推 1-9）。
    /// </summary>
    public static int AssessFragility(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return 9;
        var s = selector.Trim();

        // 扫描所有精确属性锚点 @attr='val' / @attr="val"（contains(@attr,'val') 不匹配——其用逗号非等号；normalize-space(.)='val' 的 = 前是 ) 不匹配 @）
        bool hasStableId = false, hasStableName = false, hasStableData = false, hasStableOther = false, hasDynamicAnchor = false;
        foreach (Match m in Regex.Matches(s, @"@([\w:-]+)\s*=\s*(?:'([^']*)'|""([^""]*)"")"))
        {
            var attr = m.Groups[1].Value;
            var val = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            if (!IsStableValue(val, attr)) { hasDynamicAnchor = true; continue; }  // 动态值锚点（GUID id 等）失效
            if (attr == "id") hasStableId = true;
            else if (attr == "name") hasStableName = true;
            else if (attr.StartsWith("data-", StringComparison.Ordinal)) hasStableData = true;
            else hasStableOther = true;
        }

        if (hasStableId) return 1;
        if (hasStableName) return 2;
        if (hasStableData) return 3;
        if (Regex.IsMatch(s, @"normalize-space\(\.\)\s*=\s*(?:'[^']*'|""[^""]*"")")) return 4;  // 精确文本锚点
        if (hasStableOther) return 5;
        if (s.Contains("contains(concat(' ',@class", StringComparison.Ordinal)) return 5;  // class 精确匹配（ExtractAsync priority 5）
        if (s.Contains("contains(", StringComparison.Ordinal)) return 7;  // 属性/文本包含匹配（NeedsFallback）
        if (hasDynamicAnchor) return 8;  // 仅动态值锚点（GUID id 等）→ 高脆弱
        if (s.StartsWith("/html/", StringComparison.Ordinal)) return 8;  // 完整路径
        if (s.StartsWith("(//", StringComparison.Ordinal)) return 8;  // 位置谓词 XPath (//tag)[N] → 高脆弱（无锚点靠位置）
        // CSS selector（非 XPath 语法）→ 中等（onError 兜底，不求助）；其余无锚点 XPath → 高脆弱
        if (!s.StartsWith("//", StringComparison.Ordinal) && !s.StartsWith("/", StringComparison.Ordinal) && !s.StartsWith("xpath=", StringComparison.Ordinal))
            return 7;
        return 8;
    }

    #endregion

    #region XPath 辅助方法

    /// <summary>
    /// XPath 属性值转义。返回可直接插入 XPath 的完整属性值（含引号包裹）。
    /// 不含单引号 → 'value'；含单引号不含双引号 → "value"；两种引号都有 → concat(...)。
    /// concat 优化：尽量积累最长片段（只要只含一种引号就能用另一种引号包裹），
    /// 只在添加下一个字符会导致同时含两种引号时才切分。
    /// 注意：XPath 1.0 字符串字面量中 &amp; 是合法字符，不需要特殊处理。
    /// </summary>
    /// <summary>
    /// 根据锚点相对 target 的位置(side)返回正确的 XPath 兄弟轴。
    /// 锚点在 target 前(preceding)→要用 following-sibling 才能从锚点向后够到 target；在后(following)→preceding-sibling。
    /// 方向必须反转：直接把 side 当轴名会反（阶段四 axis 方向 bug，2026-06-16 PowerShell 实证修复）。
    /// </summary>
    public static string SiblingAxisForSide(string side) =>
        side == "preceding" ? "following-sibling" : "preceding-sibling";

    public static string XpathEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "''";
        if (!value.Contains('\'')) return $"'{value}'";
        if (!value.Contains('"')) return $"\"{value}\"";
        // 两种引号都有，用 concat 拼接（最长片段策略，最小化 part 数量）
        var parts = new List<string>();
        var current = new StringBuilder();
        bool hasSingle = false;  // 当前片段是否含 '
        bool hasDouble = false;  // 当前片段是否含 "

        foreach (var c in value)
        {
            if (c == '\'')
            {
                if (hasDouble) Flush(); // 当前已含 "，再加 ' 会两种都有，先刷出
                hasSingle = true;
                current.Append(c);
            }
            else if (c == '"')
            {
                if (hasSingle) Flush(); // 当前已含 '，再加 " 会两种都有，先刷出
                hasDouble = true;
                current.Append(c);
            }
            else
            {
                current.Append(c);
            }
        }
        Flush();
        return $"concat({string.Join(", ", parts)})";

        void Flush()
        {
            if (current.Length == 0) return;
            var str = current.ToString();
            // 含 ' → 用 " 包裹；含 " → 用 ' 包裹（两者都不含理论上不可能走到这里）
            parts.Add(!hasSingle ? $"'{str}'" : $"\"{str}\"");
            current.Clear();
            hasSingle = false;
            hasDouble = false;
        }
    }

    #endregion

    public async Task<SelectorResult> ExtractAsync(ILocator locator, ILocator frameContext)
    {
        // 获取目标元素 tagName，供 Phase 1 label 关联、Phase 3/4 格式化共同使用
        var targetTag = await locator.EvaluateAsync<string>("el => el.tagName.toLowerCase()");

        var candidates = new List<(string Selector, int Priority)>();

        // 同时收集「稳定但不唯一」的属性供阶段二组合
        // predicate 是 XPath 谓词片段（如 @name='phone'），不含外层 //*[]
        var stableButNotUnique = new List<(string predicate, string attrName)>();

        // === 阶段一：自身属性精确匹配 ===

        // 优先级 1: id（动态则跳过）
        var id = await GetAttributeSafeAsync(locator, "id");
        if (!string.IsNullOrEmpty(id) && IsStableValue(id, "id"))
        {
            var xpath = $"//*[@id={XpathEscape(id)}]";
            if (await IsUnique(frameContext, xpath))
                candidates.Add((xpath, 1));
            else
                stableButNotUnique.Add(($"@id={XpathEscape(id)}", "id"));
        }

        // 优先级 2: name（动态则跳过）
        var name = await GetAttributeSafeAsync(locator, "name");
        if (!string.IsNullOrEmpty(name) && IsStableValue(name, "name"))
        {
            var xpath = $"//*[@name={XpathEscape(name)}]";
            if (await IsUnique(frameContext, xpath))
                candidates.Add((xpath, 2));
            else
                stableButNotUnique.Add(($"@name={XpathEscape(name)}", "name"));
        }

        // 优先级 3: data-* 属性
        var dataAttrs = await ExtractDataAttributesAsync(locator);
        foreach (var attr in dataAttrs)
        {
            var xpath = $"//*[@{attr.name}={XpathEscape(attr.value)}]";
            if (await IsUnique(frameContext, xpath))
                candidates.Add((xpath, 3));
            else
                stableButNotUnique.Add(($"@{attr.name}={XpathEscape(attr.value)}", "data"));
        }

        // 优先级 4: 唯一文本内容
        var text = await GetInnerTextSafeAsync(locator);
        if (!string.IsNullOrEmpty(text) && text.Length < 50)
        {
            var xpath = $"//*[normalize-space(.)={XpathEscape(text.Trim())}]";
            if (await IsUnique(frameContext, xpath))
                candidates.Add((xpath, 4));
            else
                stableButNotUnique.Add(($"normalize-space(.)={XpathEscape(text.Trim())}", "text"));
        }

        // 优先级 5: 语义定位器
        var ariaLabel = await GetAttributeSafeAsync(locator, "aria-label");
        if (!string.IsNullOrEmpty(ariaLabel))
        {
            var xpath = $"//*[@aria-label={XpathEscape(ariaLabel)}]";
            if (await IsUnique(frameContext, xpath))
                candidates.Add((xpath, 5));
            else if (IsStableValue(ariaLabel, "aria-label"))
                stableButNotUnique.Add(($"@aria-label={XpathEscape(ariaLabel)}", "aria-label"));
        }

        var placeholder = await GetAttributeSafeAsync(locator, "placeholder");
        if (!string.IsNullOrEmpty(placeholder))
        {
            var xpath = $"//*[@placeholder={XpathEscape(placeholder)}]";
            if (await IsUnique(frameContext, xpath))
                candidates.Add((xpath, 5));
            else if (IsStableValue(placeholder, "placeholder"))
                stableButNotUnique.Add(($"@placeholder={XpathEscape(placeholder)}", "placeholder"));
        }

        // type, role, title, href, alt, value
        var typeAttr = await GetAttributeSafeAsync(locator, "type");
        if (!string.IsNullOrEmpty(typeAttr) && IsStableValue(typeAttr, "type"))
        {
            var xpath = $"//*[@type={XpathEscape(typeAttr)}]";
            if (await IsUnique(frameContext, xpath))
                candidates.Add((xpath, 5));
            else
                stableButNotUnique.Add(($"@type={XpathEscape(typeAttr)}", "type"));
        }

        var roleAttr = await GetAttributeSafeAsync(locator, "role");
        if (!string.IsNullOrEmpty(roleAttr) && IsStableValue(roleAttr, "role"))
        {
            var xpath = $"//*[@role={XpathEscape(roleAttr)}]";
            if (await IsUnique(frameContext, xpath))
                candidates.Add((xpath, 5));
            else
                stableButNotUnique.Add(($"@role={XpathEscape(roleAttr)}", "role"));
        }

        var titleAttr = await GetAttributeSafeAsync(locator, "title");
        if (!string.IsNullOrEmpty(titleAttr) && IsStableValue(titleAttr, "title"))
        {
            var xpath = $"//*[@title={XpathEscape(titleAttr)}]";
            if (await IsUnique(frameContext, xpath))
                candidates.Add((xpath, 5));
            else
                stableButNotUnique.Add(($"@title={XpathEscape(titleAttr)}", "title"));
        }

        var hrefAttr = await GetAttributeSafeAsync(locator, "href");
        if (!string.IsNullOrEmpty(hrefAttr) && IsStableValue(hrefAttr, "href"))
        {
            var xpath = $"//*[@href={XpathEscape(hrefAttr)}]";
            if (await IsUnique(frameContext, xpath))
                candidates.Add((xpath, 5));
            else
                stableButNotUnique.Add(($"@href={XpathEscape(hrefAttr)}", "href"));
        }

        var altAttr = await GetAttributeSafeAsync(locator, "alt");
        if (!string.IsNullOrEmpty(altAttr) && IsStableValue(altAttr, "alt"))
        {
            var xpath = $"//*[@alt={XpathEscape(altAttr)}]";
            if (await IsUnique(frameContext, xpath))
                candidates.Add((xpath, 5));
            else
                stableButNotUnique.Add(($"@alt={XpathEscape(altAttr)}", "alt"));
        }

        var valueAttr = await GetAttributeSafeAsync(locator, "value");
        // value 属性：可输入类元素（input 文本框/textarea/select）的 value 是用户填入的业务数据，
        // 录制刚 fill/type 后即为实际值，回放时该值改变 → 不能作为稳定锚点
        // （否则产出 //*[value='张三'] 这类回放必失效的选择器）。
        // 仅按钮/选项类 input（submit/button/checkbox/radio 等）的 value 才稳定。
        if (!string.IsNullOrEmpty(valueAttr) && IsStableValue(valueAttr, "value") && !IsValueUserData(targetTag, typeAttr))
        {
            var xpath = $"//*[@value={XpathEscape(valueAttr)}]";
            if (await IsUnique(frameContext, xpath))
                candidates.Add((xpath, 5));
            else
                stableButNotUnique.Add(($"@value={XpathEscape(valueAttr)}", "value"));
        }

        // class 精确匹配（稳定 class 值参与阶段二组合）
        var classAttr = await GetAttributeSafeAsync(locator, "class");
        if (!string.IsNullOrEmpty(classAttr))
        {
            var classValues = classAttr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var cv in classValues)
            {
                var trimmed = cv.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (!IsStableValue(trimmed, "class")) continue;
                var classPredicate = $"contains(concat(' ',@class,' '),{XpathEscape($" {trimmed} ")})";
                var xpath = $"//*[{classPredicate}]";
                if (await IsUnique(frameContext, xpath))
                    candidates.Add((xpath, 5));
                else
                    stableButNotUnique.Add((classPredicate, "class"));
            }
        }

        // 有单属性精确匹配的直接返回最优结果
        if (candidates.Count > 0)
        {
            var best = candidates[0];
            var candidateList = candidates.Select(c => c.Selector).ToList();
            return new(best.Selector, best.Priority, candidateList, NeedsFallback: best.Priority >= 7);
        }

        // label 关联（在 candidates 无精确匹配后才尝试，依次测试，不用并集）
        var labelText = await FindAssociatedLabelAsync(frameContext, locator);
        if (!string.IsNullOrEmpty(labelText))
        {
            // 先试：//label[contains(.,'x')]/following-sibling::targetTag
            var labelSibling = $"//label[contains(.,{XpathEscape(labelText)})]/following-sibling::{targetTag}";
            if (await IsUnique(frameContext, labelSibling))
                return new(labelSibling, 5, NeedsFallback: false);

            // 再试：//label[contains(.,'x')]//targetTag
            var labelDesc = $"//label[contains(.,{XpathEscape(labelText)})]//{targetTag}";
            if (await IsUnique(frameContext, labelDesc))
                return new(labelDesc, 5, NeedsFallback: false);
        }

        // === 阶段二：自身属性组合 ===
        // 排除 id（稳定 id 应已唯一）和语义重复的组合
        var combinable = stableButNotUnique
            .Where(c => c.attrName != "id")
            .ToList();

        // 排除语义重复的组合
        var excludedPairs = new HashSet<(string, string)>
        {
            ("placeholder", "label"), ("text", "aria-label"),
            ("type", "role"), ("value", "text"),
        };

        if (combinable.Count >= 2)
        {
            // 按优先级排序组合
            var priorityOrder = new Dictionary<string, int>
            {
                ["name"] = 1, ["type"] = 2, ["placeholder"] = 3,
                ["data"] = 4, ["aria-label"] = 5, ["role"] = 6,
                ["title"] = 7, ["class"] = 8, ["href"] = 9, ["alt"] = 10,
                ["value"] = 11, ["text"] = 12,
            };

            var sorted = combinable
                .OrderBy(c => priorityOrder.GetValueOrDefault(c.attrName, 99))
                .ToList();

            // 两两组合
            for (int i = 0; i < sorted.Count; i++)
            {
                for (int j = i + 1; j < sorted.Count; j++)
                {
                    var a = sorted[i];
                    var b = sorted[j];

                    // 跳过语义重复的组合
                    if (excludedPairs.Contains((a.attrName, b.attrName))
                        || excludedPairs.Contains((b.attrName, a.attrName)))
                        continue;

                    var combined = $"//*[{a.predicate} and {b.predicate}]";
                    if (await IsUnique(frameContext, combined))
                    {
                        return new(combined, 5, null, NeedsFallback: false);
                    }
                }
            }

            // 三属性组合
            if (sorted.Count >= 3)
            {
                for (int i = 0; i < sorted.Count - 2; i++)
                {
                    for (int j = i + 1; j < sorted.Count - 1; j++)
                    {
                        for (int k = j + 1; k < sorted.Count; k++)
                        {
                            var combined = $"//*[{sorted[i].predicate} and {sorted[j].predicate} and {sorted[k].predicate}]";
                            if (await IsUnique(frameContext, combined))
                            {
                                return new(combined, 5, null, NeedsFallback: false);
                            }
                        }
                    }
                }
            }
        }

        // === 阶段三：祖先路径（属性锚点 + 文本锚点）===
        var phase3Result = await TryAncestorPathAsync(locator, frameContext, targetTag);
        if (phase3Result != null)
            return phase3Result;

        // === 阶段四：近邻锚点（属性锚点 + 文本锚点）===
        var phase4Result = await TryNearbyAnchorsAsync(locator, frameContext, targetTag);
        if (phase4Result != null)
            return phase4Result;

        // === 阶段五：属性包含匹配（合并单+多）===
        var containsResults = await TryContainsMatchAsync(locator, frameContext);

        // 先检查是否有唯一的
        var uniqueContains = containsResults.FirstOrDefault(r => r.isUnique);
        if (uniqueContains.predicate != null)
            return new(uniqueContains.xpath, 7, NeedsFallback: true);

        // 多属性包含组合
        var containsCombinable = containsResults
            .Where(r => !r.isUnique && r.predicate != null)
            .ToList();

        if (containsCombinable.Count >= 2)
        {
            for (int i = 0; i < containsCombinable.Count; i++)
            {
                for (int j = i + 1; j < containsCombinable.Count; j++)
                {
                    var combined = $"//*[{containsCombinable[i].predicate} and {containsCombinable[j].predicate}]";
                    if (await IsUnique(frameContext, combined))
                    {
                        return new(combined, 7, null, NeedsFallback: true);
                    }
                }
            }

            // 三属性组合
            if (containsCombinable.Count >= 3)
            {
                for (int i = 0; i < containsCombinable.Count - 2; i++)
                {
                    for (int j = i + 1; j < containsCombinable.Count - 1; j++)
                    {
                        for (int k = j + 1; k < containsCombinable.Count; k++)
                        {
                            var combined = $"//*[{containsCombinable[i].predicate} and {containsCombinable[j].predicate} and {containsCombinable[k].predicate}]";
                            if (await IsUnique(frameContext, combined))
                            {
                                return new(combined, 7, null, NeedsFallback: true);
                            }
                        }
                    }
                }
            }
        }

        // === 阶段六：XPath 完整路径 ===
        var xpathPath = await BuildXpathPathAsync(frameContext, locator);
        if (xpathPath != null && await IsUnique(frameContext, xpathPath))
            return new(xpathPath, 8, NeedsFallback: true);

        // === 阶段七：AI 兜底 ===
        return new(null, 9, NeedsAiAction: true);
    }

    #region 阶段三：祖先路径

    /// <summary>
    /// 向上遍历祖先元素，收集属性锚点和文本锚点，构建相对 XPath。
    /// 属性锚点优先（NeedsFallback=false），文本锚点其次（NeedsFallback=true）。
    /// </summary>
    private async Task<SelectorResult?> TryAncestorPathAsync(ILocator locator, ILocator frameContext, string targetTag)
    {
        try
        {
            var results = await locator.EvaluateAsync<List<Dictionary<string, JsonElement>>>(@"(el) => {
                function isStable(val) {
                    if (/[a-fA-F0-9]{8}(-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12}/.test(val)) return false;
                    if (/\d{6,}/.test(val)) return false;
                    if (/^(ext-gen|layui-layer|rc-[\w]+-)\d+$/.test(val)) return false;
                    if (/^\d+$/.test(val)) return false;
                    if (/^:r[a-z0-9]*:$/.test(val)) return false;
                    return true;
                }

                var targetText = (el.textContent || '').trim();

                const MAX_DEPTH = " + MaxAncestorDepth + @";
                const results = [];
                let depth = 0;
                let current = el.parentElement;
                while (current && depth < MAX_DEPTH) {
                    depth++;
                    const tag = current.tagName.toLowerCase();

                    if (current.id && isStable(current.id)) {
                        results.push({type: 'attr', attr: 'id', value: current.id, depth, tag});
                    }
                    var nameVal = current.getAttribute('name');
                    if (nameVal && isStable(nameVal)) {
                        results.push({type: 'attr', attr: 'name', value: nameVal, depth, tag});
                    }
                    var titleVal = current.getAttribute('title');
                    if (titleVal && isStable(titleVal)) {
                        results.push({type: 'attr', attr: 'title', value: titleVal, depth, tag});
                    }
                    for (const attr of current.attributes) {
                        if (attr.name.startsWith('data-') && isStable(attr.value)) {
                            results.push({type: 'attr', attr: attr.name, value: attr.value, depth, tag});
                        }
                    }

                    let directText = '';
                    for (const child of current.childNodes) {
                        if (child.nodeType === 3) {
                            directText += child.textContent;
                        }
                    }
                    directText = directText.trim();
                    if (directText.length > 0 && directText.length < 50 && directText !== targetText) {
                        results.push({type: 'text', value: directText, depth, tag});
                    }

                    current = current.parentElement;
                }
                return results;
            }");

            if (results == null || results.Count == 0) return null;

            // 分离属性锚点和文本锚点
            var attrAnchors = results.Where(r => r["type"].GetString() == "attr").ToList();
            var textAnchors = results.Where(r => r["type"].GetString() == "text").ToList();

            // 先试属性锚点（优先）
            foreach (var anchor in attrAnchors)
            {
                var attr = anchor["attr"].GetString();
                var value = anchor["value"].GetString();
                // 属性锚点用 * 通配（属性值已足够唯一，tagName 多余且祖先容器 tagName 易变）
                var xpath = $"//*[@{attr}={XpathEscape(value!)}]//{targetTag}";
                if (await IsUnique(frameContext, xpath))
                    return new(xpath, 6, NeedsFallback: false);
            }

            // 再试文本锚点
            foreach (var anchor in textAnchors)
            {
                var tag = anchor["tag"].GetString();
                var text = anchor["value"].GetString();
                // 祖先是容器，用 contains 更灵活容错
                var xpath = $"//{tag}[contains(.,{XpathEscape(text!)})]//{targetTag}";
                if (await IsUnique(frameContext, xpath))
                    return new(xpath, 6, NeedsFallback: true);
            }

            return null;
        }
        catch { return null; }
    }

    #endregion

    #region 阶段四：近邻锚点

    /// <summary>
    /// 遍历兄弟/附近元素，收集属性锚点和文本锚点，构建相对 XPath。
    /// 属性锚点优先（NeedsFallback=false），文本锚点其次（NeedsFallback=true）。
    /// </summary>
    private async Task<SelectorResult?> TryNearbyAnchorsAsync(ILocator locator, ILocator frameContext, string targetTag)
    {
        try
        {
            var results = await locator.EvaluateAsync<List<Dictionary<string, JsonElement>>>(@"(el) => {
                function isStable(val) {
                    if (/[a-fA-F0-9]{8}(-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12}/.test(val)) return false;
                    if (/\d{6,}/.test(val)) return false;
                    if (/^(ext-gen|layui-layer|rc-[\w]+-)\d+$/.test(val)) return false;
                    if (/^\d+$/.test(val)) return false;
                    if (/^:r[a-z0-9]*:$/.test(val)) return false;
                    return true;
                }
                function getDirectText(elem) {
                    let t = '';
                    for (const child of elem.childNodes) {
                        if (child.nodeType === 3) t += child.textContent;
                    }
                    return t.trim();
                }

                const MAX_SIBLINGS = " + MaxNearbySiblings + @";
                const MAX_ANCESTOR_SIBLINGS = " + MaxNearbyAncestorSiblings + @";
                const MAX_LEVELS = " + MaxNearbyAncestorLevels + @";
                const results = [];
                let target = el;

                for (let level = 0; level < MAX_LEVELS; level++) {
                    let parent = target.parentElement;
                    if (!parent) break;

                    let siblings = Array.from(parent.children);
                    let targetIndex = siblings.indexOf(target);
                    let limit = level === 0 ? MAX_SIBLINGS : MAX_ANCESTOR_SIBLINGS;

                    for (let i = targetIndex - 1; i >= Math.max(0, targetIndex - limit); i--) {
                        let sib = siblings[i];
                        let tag = sib.tagName.toLowerCase();
                        if (sib.id && isStable(sib.id)) results.push({level, side: 'preceding', type: 'attr', attr: 'id', value: sib.id, tag});
                        var nameVal = sib.getAttribute('name');
                        if (nameVal && isStable(nameVal)) results.push({level, side: 'preceding', type: 'attr', attr: 'name', value: nameVal, tag});
                        var titleVal = sib.getAttribute('title');
                        if (titleVal && isStable(titleVal)) results.push({level, side: 'preceding', type: 'attr', attr: 'title', value: titleVal, tag});
                        for (const a of sib.attributes) {
                            if (a.name.startsWith('data-') && isStable(a.value)) results.push({level, side: 'preceding', type: 'attr', attr: a.name, value: a.value, tag});
                        }
                        let text = getDirectText(sib);
                        if (text.length > 0 && text.length < 50) results.push({level, side: 'preceding', type: 'text', value: text, tag});
                    }

                    for (let i = targetIndex + 1; i <= Math.min(siblings.length - 1, targetIndex + limit); i++) {
                        let sib = siblings[i];
                        let tag = sib.tagName.toLowerCase();
                        if (sib.id && isStable(sib.id)) results.push({level, side: 'following', type: 'attr', attr: 'id', value: sib.id, tag});
                        var nameVal2 = sib.getAttribute('name');
                        if (nameVal2 && isStable(nameVal2)) results.push({level, side: 'following', type: 'attr', attr: 'name', value: nameVal2, tag});
                        var titleVal2 = sib.getAttribute('title');
                        if (titleVal2 && isStable(titleVal2)) results.push({level, side: 'following', type: 'attr', attr: 'title', value: titleVal2, tag});
                        for (const a of sib.attributes) {
                            if (a.name.startsWith('data-') && isStable(a.value)) results.push({level, side: 'following', type: 'attr', attr: a.name, value: a.value, tag});
                        }
                        let text2 = getDirectText(sib);
                        if (text2.length > 0 && text2.length < 50) results.push({level, side: 'following', type: 'text', value: text2, tag});
                    }

                    target = parent;
                }
                return results;
            }");

            if (results == null || results.Count == 0) return null;

            // 分离属性锚点和文本锚点
            var attrAnchors = results.Where(r => r["type"].GetString() == "attr").ToList();
            var textAnchors = results.Where(r => r["type"].GetString() == "text").ToList();

            // 先试属性锚点（优先）
            foreach (var anchor in attrAnchors)
            {
                var level = anchor["level"].GetInt32();
                var side = anchor["side"].GetString();     // "preceding"=锚点在 target 前 / "following"=在后
                var attr = anchor["attr"].GetString();
                var value = anchor["value"].GetString();
                // 轴方向必须与锚点相对 target 的位置相反：锚点在前→向后(following-sibling)够到 target，在后→向前(preceding-sibling)。
                // （原直接把 side 当轴名会反，阶段四永远匹配 0 → 静默失效；2026-06-16 实证修复）
                var axis = SiblingAxisForSide(side);
                // level==0：直接兄弟轴；level>0：跨父级叔伯，限定到兄弟分支后代（距离有界，原无界文档轴会跨级误匹配）
                // 属性锚点用 * 通配（属性值已足够唯一）
                var xpath = level == 0
                    ? $"//*[@{attr}={XpathEscape(value!)}]/{axis}::{targetTag}"
                    : $"//*[@{attr}={XpathEscape(value!)}]/{axis}::*//{targetTag}";
                if (await IsUnique(frameContext, xpath))
                    return new(xpath, 7, NeedsFallback: false);
            }

            // 再试文本锚点
            foreach (var anchor in textAnchors)
            {
                var level = anchor["level"].GetInt32();
                var side = anchor["side"].GetString();
                var tag = anchor["tag"].GetString();
                var text = anchor["value"].GetString();
                var axis = SiblingAxisForSide(side);
                // 限距策略同属性锚点：level>0 限定到兄弟分支后代。文本锚点用具体 tag（缩小匹配范围）
                var xpath = level == 0
                    ? $"//{tag}[contains(.,{XpathEscape(text!)})]/{axis}::{targetTag}"
                    : $"//{tag}[contains(.,{XpathEscape(text!)})]/{axis}::*//{targetTag}";
                if (await IsUnique(frameContext, xpath))
                    return new(xpath, 7, NeedsFallback: true);
            }

            return null;
        }
        catch { return null; }
    }

    #endregion

    #region 阶段五：属性包含匹配

    /// <summary>
    /// 包含匹配：遍历元素属性，去掉动态部分后用 contains(@attr,'稳定部分') 检查唯一性。
    /// 返回候选列表（含 XPath 谓词片段 + 完整 XPath + 是否唯一标记），供多属性包含组合使用。
    /// </summary>
    private async Task<List<(string predicate, string xpath, string attrName, bool isUnique)>> TryContainsMatchAsync(
        ILocator locator, ILocator frameContext)
    {
        var results = new List<(string predicate, string xpath, string attrName, bool isUnique)>();
        try
        {
            var attrs = await locator.EvaluateAsync<List<Dictionary<string, string>>>(@"(el) => {
                const result = [];
                const priorityAttrs = ['id', 'name', 'class', 'data-testid', 'title', 'src', 'href', 'type', 'role'];
                for (const attrName of priorityAttrs) {
                    const val = el.getAttribute(attrName);
                    if (val) result.push({name: attrName, value: val});
                }
                // 其他 data-* 属性
                for (const attr of el.attributes) {
                    if (attr.name.startsWith('data-') && !priorityAttrs.includes(attr.name) && attr.value) {
                        result.push({name: attr.name, value: attr.value});
                    }
                }
                return result;
            }");

            if (attrs == null) return results;

            foreach (var attr in attrs)
            {
                var attrName = attr["name"];
                var attrValue = attr["value"];
                if (string.IsNullOrEmpty(attrName) || string.IsNullOrEmpty(attrValue)) continue;

                // 如果值本身稳定且唯一，前面优先级已经处理了，这里只处理动态值
                if (IsStableValue(attrValue, attrName)) continue;

                var stablePart = TryExtractStablePart(attrValue, attrName);
                if (stablePart == null || stablePart.Length < 2) continue;

                var predicate = $"contains(@{attrName},{XpathEscape(stablePart)})";
                var xpath = $"//*[contains(@{attrName},{XpathEscape(stablePart)})]";
                var isUnique = await IsUnique(frameContext, xpath);
                results.Add((predicate, xpath, attrName, isUnique));
            }
        }
        catch { }

        return results;
    }

    #endregion

    #region 阶段六：XPath 完整路径

    static async Task<string?> BuildXpathPathAsync(ILocator frameContext, ILocator locator)
    {
        try
        {
            return await locator.EvaluateAsync<string?>(@"(el) => {
                let parts = [];
                while (el && el.nodeType === 1) {
                    let tag = el.tagName.toLowerCase();
                    if (tag === 'html') { parts.unshift(tag); break; }
                    let parent = el.parentElement;
                    if (parent) {
                        let siblings = Array.from(parent.children).filter(c => c.tagName === el.tagName);
                        if (siblings.length > 1) {
                            let idx = siblings.indexOf(el) + 1;
                            tag += '[' + idx + ']';
                        }
                    }
                    parts.unshift(tag);
                    el = parent;
                }
                return '/' + parts.join('/');
            }");
        }
        catch { return null; }
    }

    #endregion

    #region 通用辅助方法

    static async Task<bool> IsUnique(ILocator frameContext, string selector)
    {
        try { return await frameContext.Locator(selector).CountAsync() == 1; }
        catch { return false; }
    }

    static async Task<string?> GetAttributeSafeAsync(ILocator locator, string attr)
    {
        try { return await locator.GetAttributeAsync(attr); }
        catch { return null; }
    }

    static async Task<string?> GetInnerTextSafeAsync(ILocator locator)
    {
        try { return (await locator.InnerTextAsync())?.Trim(); }
        catch { return null; }
    }

    /// <summary>
    /// 提取元素的 data-* 属性，返回原始 {name, value} 键值对。
    /// JS 只返回数据，C# 端负责格式化为 XPath predicate。
    /// </summary>
    static async Task<List<(string name, string value)>> ExtractDataAttributesAsync(ILocator locator)
    {
        var results = new List<(string name, string value)>();
        try
        {
            var attrs = await locator.EvaluateAsync<List<Dictionary<string, string>>>(@"(el) => {
                const result = [];
                for (const attr of el.attributes) {
                    if (attr.name.startsWith('data-') && attr.value) {
                        result.push({name: attr.name, value: attr.value});
                    }
                }
                return result;
            }");
            if (attrs != null)
                foreach (var attr in attrs)
                    results.Add((attr["name"], attr["value"]));
        }
        catch { }
        return results;
    }

    static async Task<string?> FindAssociatedLabelAsync(ILocator frameContext, ILocator locator)
    {
        try
        {
            return await locator.EvaluateAsync<string?>(@"(el) => {
                if (el.id) {
                    const label = document.querySelector('label[for=""' + el.id + '""]');
                    if (label) return label.textContent.trim();
                }
                const parentLabel = el.closest('label');
                if (parentLabel) return parentLabel.textContent.trim();
                return null;
            }");
        }
        catch { return null; }
    }

    /// <summary>
    /// 抓取元素 outerHTML 全量（含子级内容），用于 priority 9 / priority 8 脆弱求助时给 AI 喂代码 HTML（放法 X (l) 选项，2026-07-14）。
    /// 放法 X 块A #9 决策：抓真 el.outerHTML 全量（不在 JS 截断）。方法返回单一 string 存 OperateResult.HtmlContext，
    /// 被 warning（给用户，caller 截前 50 字）与 reply 追加（给 AI，全量）共用--单一值无法同时全量+截断，故 JS 抓全量、截断由 caller 分别处理。
    /// 原"合成骨架 &lt;tag attrs&gt;...&lt;/tag&gt;"（无子级无文本）对 AI 写稳定 selector 无用，改抓真 outerHTML。
    /// </summary>
    public async Task<string?> CaptureElementContextAsync(ILocator locator)
    {
        try
        {
            return await locator.EvaluateAsync<string>("el => el.outerHTML");
        }
        catch { return null; }
    }

    #endregion
}
