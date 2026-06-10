using Microsoft.Playwright;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace SmartFilling.Engine.Tests.Helpers;

/// <summary>
/// 创建用于控制流测试的 Mock IPage。
/// 控制 goto/retry/fallback 测试中 step 使用 check(always) 不需要真实 DOM 操作。
/// </summary>
public static class MockPageFactory
{
    public static IPage Create(out IPage page)
    {
        var mockPage = Substitute.For<IPage>();

        // 基础属性
        mockPage.Url.Returns("about:blank");

        // Context.Pages 返回包含自身的列表 + Browser.IsConnected = true
        var browserMock = Substitute.For<IBrowser>();
        browserMock.IsConnected.Returns(true);
        var contextMock = Substitute.For<IBrowserContext>();
        contextMock.Browser.Returns(browserMock);
        var pagesList = new List<IPage>();
        contextMock.Pages.Returns(pagesList);
        mockPage.Context.Returns(contextMock);
        pagesList.Add(mockPage);

        // Frame 相关
        mockPage.MainFrame.Returns(Substitute.For<IFrame>());

        // Locator 链 - 返回可链式调用的 substitute
        var bodyLocator = Substitute.For<ILocator>();
        bodyLocator.CountAsync().Returns(1);
        mockPage.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>())
            .Returns(bodyLocator);

        // SetViewportSizeAsync - 默认 no-op（NSubstitute 未配置的 Task 方法返 CompletedTask）

        page = mockPage;
        return mockPage;
    }

    /// <summary>
    /// 创建会抛异常的 Mock（用于测试 retry/fallback）。
    /// fill/click 等 action 执行时 Locator 会抛 TimeoutException。
    /// </summary>
    public static IPage CreateFailing(out IPage page, string errorMessage = "element not found")
    {
        var mockPage = Create(out page);

        // 让所有 Locator 操作抛异常（后配置覆盖 Create 里的 bodyLocator）。
        // NSubstitute loose substitute 对未配置的 ILocator.Locator/First/Last 返递归 substitute（非 null），
        // 其 FillAsync 等返默认 ValueTask 不抛 -> fill 成功不触发 onError（silent）。
        // 故需显式配置 Locator/First/Last 返 failingLocator 自身，让 frame.Locator(sel).First/.Last.FillAsync
        // 都走到下面的 Throws（Moq loose 返 null->NRE 是隐式触发，NSubstitute 须显式递归 + Throws）。
        var failingLocator = Substitute.For<ILocator>();
        failingLocator.Locator(Arg.Any<string>(), Arg.Any<LocatorLocatorOptions?>()).Returns(failingLocator);
        failingLocator.First.Returns(failingLocator);
        failingLocator.Last.Returns(failingLocator);
        failingLocator.FillAsync(Arg.Any<string>(), Arg.Any<LocatorFillOptions?>())
            .Throws(new TimeoutException(errorMessage));
        failingLocator.ClickAsync(Arg.Any<LocatorClickOptions?>())
            .Throws(new TimeoutException(errorMessage));
        failingLocator.CountAsync()
            .Throws(new TimeoutException(errorMessage));

        mockPage.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>())
            .Returns(failingLocator);

        return mockPage;
    }
}
