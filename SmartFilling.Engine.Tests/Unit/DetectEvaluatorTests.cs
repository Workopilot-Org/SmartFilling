using Microsoft.Playwright;
using NSubstitute;
using SmartFilling.Engine.Engine;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;

namespace SmartFilling.Engine.Tests.Unit;

public class DetectEvaluatorTests
{
    private readonly DetectEvaluator _evaluator = new(new NullLogger());

    /// <summary>
    /// Creates a Mock IPage with basic setup (Url, Locator("body"), Context, MainFrame).
    /// </summary>
    private static IPage CreateMockPage(out IPage page, string url = "about:blank")
    {
        var mockPage = Substitute.For<IPage>();
        mockPage.Url.Returns(url);

        // Context.Pages
        var contextMock = Substitute.For<IBrowserContext>();
        var pagesList = new List<IPage> { mockPage };
        contextMock.Pages.Returns(pagesList);
        mockPage.Context.Returns(contextMock);

        // MainFrame
        mockPage.MainFrame.Returns(Substitute.For<IFrame>());

        // Locator for FrameResolver - use Arg.Any to avoid optional parameter issues in expression trees
        var bodyLocator = Substitute.For<ILocator>();
        bodyLocator.CountAsync().Returns(1);
        mockPage.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>())
            .Returns(bodyLocator);

        page = mockPage;
        return mockPage;
    }

    private static Task<bool> EvaluateAsync(
        DetectCondition detect,
        string url = "about:blank",
        Dictionary<string, object>? vars = null,
        List<Dictionary<string, object>>? scopeChain = null)
    {
        var evaluator = new DetectEvaluator(new NullLogger());
        CreateMockPage(out var page, url);
        var script = new ScriptV2();
        vars ??= new Dictionary<string, object>();
        scopeChain ??= new List<Dictionary<string, object>>();
        return evaluator.EvaluateAsync(detect, page, script, null, vars, scopeChain);
    }

    // =====================================================================
    // always / data_exists
    // =====================================================================

    [Fact]
    public async Task Evaluate_Always_ReturnsTrue()
    {
        var result = await EvaluateAsync(new DetectCondition { Type = "always" });
        Assert.True(result);
    }

    [Fact]
    public async Task Evaluate_DataExists_FieldPresent()
    {
        var scopeChain = new List<Dictionary<string, object>>
        {
            new() { { "name", "Alice" } }
        };
        var result = await EvaluateAsync(
            new DetectCondition { Type = "data_exists", Field = "name" },
            scopeChain: scopeChain);
        Assert.True(result);
    }

    [Fact]
    public async Task Evaluate_DataExists_FieldMissing()
    {
        var scopeChain = new List<Dictionary<string, object>>
        {
            new() { { "name", "Alice" } }
        };
        var result = await EvaluateAsync(
            new DetectCondition { Type = "data_exists", Field = "age" },
            scopeChain: scopeChain);
        Assert.False(result);
    }

    [Fact]
    public async Task Evaluate_DataExists_FieldEmpty()
    {
        var scopeChain = new List<Dictionary<string, object>>
        {
            new() { { "name", "" } }
        };
        var result = await EvaluateAsync(
            new DetectCondition { Type = "data_exists", Field = "name" },
            scopeChain: scopeChain);
        Assert.False(result);
    }

    [Fact]
    public async Task Evaluate_DataExists_FieldNull()
    {
        var scopeChain = new List<Dictionary<string, object>>
        {
            new() { { "name", null! } }
        };
        var result = await EvaluateAsync(
            new DetectCondition { Type = "data_exists", Field = "name" },
            scopeChain: scopeChain);
        Assert.False(result);
    }

    [Fact]
    public async Task Evaluate_DataExists_SearchesScopeChain()
    {
        // Field in inner scope (first element) should be found
        var scopeChain = new List<Dictionary<string, object>>
        {
            new() { { "amount", 200 } },
            new() { { "other", "value" } }
        };
        var result = await EvaluateAsync(
            new DetectCondition { Type = "data_exists", Field = "amount" },
            scopeChain: scopeChain);
        Assert.True(result);
    }

    // =====================================================================
    // js conditions
    // =====================================================================

    [Fact]
    public async Task Evaluate_Js_TrueExpression()
    {
        var result = await EvaluateAsync(
            new DetectCondition { Type = "js", Check = "1 === 1" });
        Assert.True(result);
    }

    [Fact]
    public async Task Evaluate_Js_FalseExpression()
    {
        var result = await EvaluateAsync(
            new DetectCondition { Type = "js", Check = "1 === 2" });
        Assert.False(result);
    }

    [Fact]
    public async Task Evaluate_Js_AccessFillData()
    {
        // Jint uses scopeChain[^1] as fillData
        var scopeChain = new List<Dictionary<string, object>>
        {
            new() { { "amount", 200 } }
        };
        var result = await EvaluateAsync(
            new DetectCondition { Type = "js", Check = "fillData.amount > 100" },
            scopeChain: scopeChain);
        Assert.True(result);
    }

    [Fact]
    public async Task Evaluate_Js_AccessVars()
    {
        var vars = new Dictionary<string, object> { { "status", "ok" } };
        var result = await EvaluateAsync(
            new DetectCondition { Type = "js", Check = "vars.status === 'ok'" },
            vars: vars);
        Assert.True(result);
    }

    [Fact]
    public async Task Evaluate_Js_InvalidExpression_ReturnsFalse()
    {
        var result = await EvaluateAsync(
            new DetectCondition { Type = "js", Check = "invalid {{{" });
        Assert.False(result);
    }

    // =====================================================================
    // combination conditions (all / any / not)
    // =====================================================================

    [Fact]
    public async Task Evaluate_All_AllTrue()
    {
        var detect = new DetectCondition
        {
            All = [new DetectCondition { Type = "always" }, new DetectCondition { Type = "always" }]
        };
        var result = await EvaluateAsync(detect);
        Assert.True(result);
    }

    [Fact]
    public async Task Evaluate_All_OneFalse()
    {
        var scopeChain = new List<Dictionary<string, object>>
        {
            new() { { "name", "Alice" } }
        };
        var detect = new DetectCondition
        {
            All =
            [
                new DetectCondition { Type = "always" },
                new DetectCondition { Type = "data_exists", Field = "missing_field" }
            ]
        };
        var result = await EvaluateAsync(detect, scopeChain: scopeChain);
        Assert.False(result);
    }

    [Fact]
    public async Task Evaluate_Any_OneTrue()
    {
        var detect = new DetectCondition
        {
            Any =
            [
                new DetectCondition { Type = "data_exists", Field = "missing" },
                new DetectCondition { Type = "always" }
            ]
        };
        var result = await EvaluateAsync(detect);
        Assert.True(result);
    }

    [Fact]
    public async Task Evaluate_Any_AllFalse()
    {
        var detect = new DetectCondition
        {
            Any =
            [
                new DetectCondition { Type = "data_exists", Field = "a_missing" },
                new DetectCondition { Type = "data_exists", Field = "b_missing" }
            ]
        };
        var result = await EvaluateAsync(detect);
        Assert.False(result);
    }

    [Fact]
    public async Task Evaluate_Not_InvertsTrue()
    {
        var detect = new DetectCondition
        {
            Not = new DetectCondition { Type = "always" }
        };
        var result = await EvaluateAsync(detect);
        Assert.False(result);
    }

    [Fact]
    public async Task Evaluate_Not_InvertsFalse()
    {
        var detect = new DetectCondition
        {
            Not = new DetectCondition { Type = "data_exists", Field = "nonexistent" }
        };
        var result = await EvaluateAsync(detect);
        Assert.True(result);
    }

    [Fact]
    public async Task Evaluate_NestedCombo()
    {
        // all=[any=[always, data_exists(missing)], not={data_exists(missing)}]
        // inner any: always=true => any=true
        // inner not: data_exists(missing)=false => not=true
        // all=[true, true] => true
        var detect = new DetectCondition
        {
            All =
            [
                new DetectCondition
                {
                    Any =
                    [
                        new DetectCondition { Type = "always" },
                        new DetectCondition { Type = "data_exists", Field = "missing" }
                    ]
                },
                new DetectCondition
                {
                    Not = new DetectCondition { Type = "data_exists", Field = "missing" }
                }
            ]
        };
        var result = await EvaluateAsync(detect);
        Assert.True(result);
    }

    // =====================================================================
    // url_changed / url_contains (require Mock IPage.Url)
    // =====================================================================

    [Fact]
    public async Task Evaluate_UrlChanged_SameUrl_ReturnsFalse()
    {
        var vars = new Dictionary<string, object> { { "_lastUrl", "about:blank" } };
        var result = await EvaluateAsync(
            new DetectCondition { Type = "url_changed" },
            url: "about:blank",
            vars: vars);
        Assert.False(result);
    }

    [Fact]
    public async Task Evaluate_UrlChanged_DifferentUrl_ReturnsTrue()
    {
        var vars = new Dictionary<string, object> { { "_lastUrl", "http://old" } };
        var result = await EvaluateAsync(
            new DetectCondition { Type = "url_changed" },
            url: "http://new",
            vars: vars);
        Assert.True(result);
    }

    [Fact]
    public async Task Evaluate_UrlContains_Match()
    {
        var result = await EvaluateAsync(
            new DetectCondition { Type = "url_contains", Value = "login" },
            url: "http://example.com/login");
        Assert.True(result);
    }

    [Fact]
    public async Task Evaluate_UrlContains_NoMatch()
    {
        var result = await EvaluateAsync(
            new DetectCondition { Type = "url_contains", Value = "admin" },
            url: "http://example.com/login");
        Assert.False(result);
    }
}
