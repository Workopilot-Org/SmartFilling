using Microsoft.Playwright;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SmartFilling.App.Recording;
using Xunit;

namespace SmartFilling.App.Tests;

/// <summary>
/// 放法 X 块A（2026-07-14）：selector↔iframe 代码提取信息回传 AI 对称补齐的纯函数 + CaptureElementContextAsync 回归测试。
/// 覆盖 R10 五类：ParseReplyWithRest（a-l 识别+rest 提取）/ UserPastedHtml（XPath 守卫+HTML 标签+残缺）/ AppendHelpHtmlIfNeeded（(l) 喂全量+(b) 粘/没粘分流+必填提示）/ TruncateForUser（50 字截断）/ ReplyChoseOption 一致性 / CaptureElementContextAsync 改真 outerHTML。
/// 生产 reply 形态："用户回答: {answer}"（ExecuteRequestHelpAsync 返回），用真实形态测（非理想化构造）。
/// </summary>
public class SparklingBlockAHtmlHelpTests
{
    // ===== ParseReplyWithRest（R6 单一解析源，a-l 识别）=====

    [Fact]
    public void ParseReplyWithRest_SingleLetter_Recognizes()
    {
        var (opt, rest) = RecordingEngine.ParseReplyWithRest("用户回答: l");
        Assert.Equal('l', opt);  // 单字 l 识别（R1：原 a-k 漏 l 已修）
        Assert.Equal("", rest);
    }

    [Fact]
    public void ParseReplyWithRest_LetterWithSeparator_RecognizesRest()
    {
        var (opt, rest) = RecordingEngine.ParseReplyWithRest("用户回答: b <div>菜单</div>");
        Assert.Equal('b', opt);
        Assert.Equal("<div>菜单</div>", rest);  // rest 提取（去字母+分隔符）
    }

    [Fact]
    public void ParseReplyWithRest_ParenForm_Recognizes()
    {
        var (opt, rest) = RecordingEngine.ParseReplyWithRest("用户回答: (l)");
        Assert.Equal('l', opt);  // (X) 形态识别
        Assert.Equal("", rest);
    }

    [Fact]
    public void ParseReplyWithRest_NoLetter_ReturnsNullOption()
    {
        var (opt, rest) = RecordingEngine.ParseReplyWithRest("用户回答: 这个元素在父级div");
        Assert.Null(opt);  // 首字母非 a-l -> 未选字母（作 k 其它）
        Assert.Equal("这个元素在父级div", rest);  // rest=整个 s
    }

    [Fact]
    public void ParseReplyWithRest_CaseInsensitive()
    {
        var (opt, _) = RecordingEngine.ParseReplyWithRest("用户回答: L");  // 大写 L -> 小写 l
        Assert.Equal('l', opt);
    }

    [Fact]
    public void ParseReplyWithRest_PrefixAbsent_AlsoWorks()
    {
        var (opt, _) = RecordingEngine.ParseReplyWithRest("f");  // 无"用户回答:"前缀也能解析
        Assert.Equal('f', opt);
    }

    // ===== UserPastedHtml（R8：XPath 守卫 + HTML 标签 + 残缺）=====

    [Fact]
    public void UserPastedHtml_HtmlTag_True()
    {
        Assert.True(RecordingEngine.UserPastedHtml("<div class=\"menu\">内容</div>"));
        Assert.True(RecordingEngine.UserPastedHtml("<input type=\"text\"/>"));
    }

    [Fact]
    public void UserPastedHtml_PureText_False()
    {
        Assert.False(RecordingEngine.UserPastedHtml("这个元素在父级div"));  // 纯文字无 <>
        Assert.False(RecordingEngine.UserPastedHtml(""));
        Assert.False(RecordingEngine.UserPastedHtml(null));
    }

    [Fact]
    public void UserPastedHtml_XPathGuard_False()
    {
        // XPath 守卫：以 // 或 / 开头且不含 < -> XPath 非 HTML，不算粘（防选 (b) 误粘 XPath）
        Assert.False(RecordingEngine.UserPastedHtml("//div[@class='x']"));
        Assert.False(RecordingEngine.UserPastedHtml("/html/body/div"));
    }

    [Fact]
    public void UserPastedHtml_FragmentAngle_True()
    {
        // 残缺 < 或 > -> 保守判粘了
        Assert.True(RecordingEngine.UserPastedHtml("<div class="));  // 残缺标签
        Assert.True(RecordingEngine.UserPastedHtml("点击 > 按钮"));  // 含 >
    }

    // ===== AppendHelpHtmlIfNeeded（R4 #3：(l)/(b) 分流）=====

    [Fact]
    public void AppendHelpHtmlIfNeeded_ChooseL_FeedsFullHtml()
    {
        var fullHtml = "<div class=\"el-submenu__title\" style=\"color:red\"><span>菜单项</span><i class=\"arrow\"></i></div>";
        var reply = RecordingEngine.AppendHelpHtmlIfNeeded("用户回答: l", fullHtml, "//div", new[] { "//iframe[@id='f']" });
        Assert.Contains(fullHtml, reply);  // 选 (l) 喂全量 outerHTML（验值：含完整 HTML 含子级，不截断）
        Assert.Contains("[代码已提取]", reply);
        Assert.Contains("已生成 selector://div", reply);
        Assert.Contains("iframe 链://iframe[@id='f']", reply);  // 代码用半角冒号
    }

    [Fact]
    public void AppendHelpHtmlIfNeeded_ChooseL_NullHtml_NoFeed()
    {
        // htmlForAi=null（不该发生--options 不含 l 时不显示）-> 原样返回不喂（防 silent）
        var reply = RecordingEngine.AppendHelpHtmlIfNeeded("用户回答: l", null, "//div", null);
        Assert.Equal("用户回答: l", reply);
    }

    [Fact]
    public void AppendHelpHtmlIfNeeded_ChooseB_Pasted_UserPrimary()
    {
        // 选 (b) 粘了 HTML -> 用户为主，不重复喂代码 HTML
        var reply = RecordingEngine.AppendHelpHtmlIfNeeded("用户回答: b <div class=\"my\">手粘的</div>", "<代码的HTML>", "//div", null);
        Assert.Contains("以你为主", reply);  // 用户为主
        Assert.DoesNotContain("<代码的HTML>", reply);  // 不重复喂代码 HTML（避免冲突）
        Assert.Contains("已生成 selector://div", reply);  // 仍给 selector 参考
    }

    [Fact]
    public void AppendHelpHtmlIfNeeded_ChooseB_NotPasted_RequiredPrompt()
    {
        // 选 (b) 没粘 HTML -> 必填提示，不喂代码 HTML（b 必填不兜底，用户 2026-07-14 拍板；后端防绕过）
        var reply = RecordingEngine.AppendHelpHtmlIfNeeded("用户回答: b", "<代码的HTML>", "//div", null);
        Assert.Contains("必须填 HTML", reply);  // 必填提示
        Assert.DoesNotContain("<代码的HTML>", reply);  // 不喂代码 HTML
        Assert.Contains("(l)", reply);  // 提示可选 (l)
    }

    [Fact]
    public void AppendHelpHtmlIfNeeded_ChooseB_PureText_NotPasted()
    {
        // 选 (b) + 纯文字描述（无 HTML）-> 视为没粘 -> 必填提示
        var reply = RecordingEngine.AppendHelpHtmlIfNeeded("用户回答: b 这个元素在父级div", "<代码的HTML>", "//div", null);
        Assert.Contains("必须填 HTML", reply);
    }

    [Fact]
    public void AppendHelpHtmlIfNeeded_ChooseB_XPath_NotPasted()
    {
        // 选 (b) + XPath（守卫判没粘）-> 必填提示（XPath 非 HTML）
        var reply = RecordingEngine.AppendHelpHtmlIfNeeded("用户回答: b //div[@class='x']", "<代码的HTML>", "//div", null);
        Assert.Contains("必须填 HTML", reply);
    }

    [Fact]
    public void AppendHelpHtmlIfNeeded_OtherOption_Original()
    {
        // 选 (a)/(c)/(d)/(e)/(f)/(h) 等 -> 原样返回（不喂代码 HTML）
        var reply = RecordingEngine.AppendHelpHtmlIfNeeded("用户回答: a", "<代码的HTML>", "//div", null);
        Assert.Equal("用户回答: a", reply);
    }

    // ===== TruncateForUser（R3：给用户截前 50 字）=====

    [Fact]
    public void TruncateForUser_Over50_TruncatesWithEllipsis()
    {
        var longHtml = "<div class=\"" + new string('a', 80) + "\">内容</div>";  // >50 字符
        var truncated = RecordingEngine.TruncateForUser(longHtml);
        Assert.True(truncated.Length <= 53);  // 50 + "..."
        Assert.EndsWith("...", truncated);
        Assert.DoesNotContain("内容", truncated);  // 截断后不含尾部内容（给用户预览，AI 拿全量）
    }

    [Fact]
    public void TruncateForUser_Short_NoTruncate()
    {
        Assert.Equal("<div>短</div>", RecordingEngine.TruncateForUser("<div>短</div>"));
        Assert.Equal("", RecordingEngine.TruncateForUser(null));
    }

    // ===== ReplyChoseOption 与 ParseReplyWithRest 一致性（R6 单一解析源）=====

    [Theory]
    [InlineData("用户回答: l", 'l')]
    [InlineData("用户回答: b <div>", 'b')]
    [InlineData("用户回答: (f)", 'f')]
    [InlineData("用户回答: h", 'h')]
    [InlineData("用户回答: 其它描述", 'f')]  // 未选字母 -> 任何 option 都返 false
    [InlineData("用户回答: a 重新定位", 'a')]
    public void ReplyChoseOption_ConsistentWithParseReplyWithRest(string reply, char option)
    {
        var (parsedOpt, _) = RecordingEngine.ParseReplyWithRest(reply);
        var replyChose = typeof(RecordingEngine).GetMethod("ReplyChoseOption", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { reply, option });
        // ReplyChoseOption 是 private static，经反射调用验一致性（单一解析源：两者判定一致，防 (b) 分流与 (f)/(h) 判断漂移）
        Assert.Equal(parsedOpt.HasValue && parsedOpt.Value == char.ToLowerInvariant(option), replyChose);
    }

    // ===== CaptureElementContextAsync 改真 outerHTML 回归（R2/#9）=====

    [Fact]
    public async Task CaptureElementContextAsync_ReturnsFullOuterHTML_NotSyntheticSkeleton()
    {
        var extractor = new SelectorExtractor();
        var locMock = Substitute.For<ILocator>();
        // 真实 outerHTML（含子级内容 + 文本），非原合成骨架 <tag attrs>...</tag>
        var fullHtml = "<div class=\"el-submenu__title\" style=\"color:red\"><span class=\"title\">深层输入</span><input type=\"text\"/></div>";
        locMock.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<LocatorEvaluateOptions?>()).Returns(fullHtml);

        var result = await extractor.CaptureElementContextAsync(locMock);

        Assert.Equal(fullHtml, result);  // 全量 outerHTML（验值：等于真实 outerHTML，非合成骨架）
        Assert.Contains("<span", result);  // 含子级（原合成骨架 <div attrs>...</div> 无子级）
        Assert.Contains("深层输入", result);  // 含文本内容
        Assert.DoesNotContain("...</div>", result);  // 非合成骨架的 ... 占位
    }

    [Fact]
    public async Task CaptureElementContextAsync_EvaluateThrows_ReturnsNull()
    {
        var extractor = new SelectorExtractor();
        var locMock = Substitute.For<ILocator>();
        locMock.EvaluateAsync<string>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<LocatorEvaluateOptions?>()).Throws(new Exception("frame detached"));

        var result = await extractor.CaptureElementContextAsync(locMock);

        Assert.Null(result);  // 异常返 null 不崩（caller 防御）
    }
}
