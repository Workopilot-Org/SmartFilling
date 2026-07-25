using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using SmartFilling.Engine.Ai;
using SmartFilling.Engine.Models;
using SmartFilling.Engine.Tests.Helpers;
using Xunit;

namespace SmartFilling.Engine.Tests.Ai;

/// <summary>
/// OpenAiProvider 重试 + 断路器协同单元测试（playful-greeting-diffie 计划）。
/// 不 mock ChatClient（难），改测 3 个可测面：
/// 1. IsRetryable 纯函数（异常分类）
/// 2. ComputeBackoffDelay 纯函数（指数退避 + 封顶）
/// 3. RetryWithBreakerAsync internal 方法（注入 attemptFactory 验断路器协同：一次逻辑失败 = 1 次计数）
/// </summary>
public class OpenAiProviderRetryTests
{
    // ============ IsRetryable 异常分类 ============

    [Fact]
    public void IsRetryable_OperationCanceledException_ReturnsFalse()
    {
        // ct 取消不重试（catch 子句硬拦截，此处保守 false 兜底）
        Assert.False(OpenAiProvider.IsRetryable(new OperationCanceledException()));
        Assert.False(OpenAiProvider.IsRetryable(new TaskCanceledException()));  // TCE 派生 OCE
    }

    [Fact]
    public void IsRetryable_InvalidOperationException_ReturnsFalse()
    {
        // IOE 不重试（catch 子句硬拦截，含断路器自身）
        Assert.False(OpenAiProvider.IsRetryable(new InvalidOperationException("breaker")));
    }

    [Fact]
    public void IsRetryable_HttpRequestException_NoStatusCode_ReturnsTrue()
    {
        // 纯网络层（连接失败/抖动），无 status → 可重试
        var ex = new HttpRequestException("connection refused");
        Assert.Null(ex.StatusCode);  // 前置确认无 status
        Assert.True(OpenAiProvider.IsRetryable(ex));
    }

    [Theory]
    [InlineData(429)]  // 限流
    [InlineData(500)]  // 服务端临时
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void IsRetryable_HttpRequestException_RetryableStatus_ReturnsTrue(int status)
    {
        var ex = new HttpRequestException("err", null, (HttpStatusCode)status);
        Assert.Equal(status, (int)ex.StatusCode!.Value);
        Assert.True(OpenAiProvider.IsRetryable(ex));
    }

    [Theory]
    [InlineData(400)]  // 请求错误
    [InlineData(401)]  // 鉴权失败
    [InlineData(403)]  // 禁止
    [InlineData(404)]  // 未找到
    [InlineData(200)]  // 正常响应（理论上不会进异常，但 IsRetryable 须保守 false）
    public void IsRetryable_HttpRequestException_NonRetryableStatus_ReturnsFalse(int status)
    {
        var ex = new HttpRequestException("err", null, (HttpStatusCode)status);
        Assert.False(OpenAiProvider.IsRetryable(ex));
    }

    [Theory]
    [InlineData(429)]  // 限流
    [InlineData(500)]  // 服务端临时
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void IsRetryable_ClientResultException_RetryableStatus_ReturnsTrue(int status)
    {
        var ex = CreateClientResultException(status);
        Assert.Equal(status, ex.Status);
        Assert.True(OpenAiProvider.IsRetryable(ex));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public void IsRetryable_ClientResultException_NonRetryableStatus_ReturnsFalse(int status)
    {
        var ex = CreateClientResultException(status);
        Assert.Equal(status, ex.Status);
        Assert.False(OpenAiProvider.IsRetryable(ex));
    }

    [Fact]
    public void IsRetryable_UnknownException_ReturnsFalse()
    {
        // 未知异常保守不重试
        Assert.False(OpenAiProvider.IsRetryable(new ApplicationException("unknown")));
        Assert.False(OpenAiProvider.IsRetryable(new FormatException("parse err")));
    }

    /// <summary>
    /// 构造 ClientResultException（OpenAI SDK 2.9.1 基于 System.ClientModel 1.8.1）。
    /// ClientResultException 构造函数需 PipelineResponse，Status 从 response.Status 派生。
    /// 用 NSubstitute mock PipelineResponse + 配置 Status 返回值。
    /// </summary>
    private static ClientResultException CreateClientResultException(int status, string message = "test")
    {
        var response = Substitute.For<PipelineResponse>();
        response.Status.Returns(status);
        return new ClientResultException(message, response, null);
    }

    // ============ IsRetryableStatus 直接分类测试 ============

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(599, true)]  // 5xx 上界
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(200, false)]
    [InlineData(0, false)]  // 0（status 未设置）保守 false
    [InlineData(600, false)]  // 5xx 上界之外
    public void IsRetryableStatus_Classifies(int status, bool expected)
    {
        Assert.Equal(expected, OpenAiProvider.IsRetryableStatus(status));
    }

    // ============ ComputeBackoffDelay 指数退避 + 封顶 ============

    [Fact]
    public void ComputeBackoffDelay_Base1000_Attempt0_Returns1000()
    {
        Assert.Equal(1000, OpenAiProvider.ComputeBackoffDelay(1000, 0));  // 1000 * 2^0 = 1000
    }

    [Fact]
    public void ComputeBackoffDelay_Base1000_Attempt1_Returns2000()
    {
        Assert.Equal(2000, OpenAiProvider.ComputeBackoffDelay(1000, 1));  // 1000 * 2^1 = 2000
    }

    [Fact]
    public void ComputeBackoffDelay_Base1000_Attempt2_Returns4000()
    {
        Assert.Equal(4000, OpenAiProvider.ComputeBackoffDelay(1000, 2));  // 1000 * 2^2 = 4000
    }

    [Fact]
    public void ComputeBackoffDelay_CapsAt60000()
    {
        // base=1000, attempt=10 → 1000*1024 = 1024000，封顶 60000
        Assert.Equal(60000, OpenAiProvider.ComputeBackoffDelay(1000, 10));
        // base=50000, attempt=1 → 50000*2 = 100000，封顶 60000
        Assert.Equal(60000, OpenAiProvider.ComputeBackoffDelay(50000, 1));
        // base=60000, attempt=0 → 60000（恰好等于封顶，不超）
        Assert.Equal(60000, OpenAiProvider.ComputeBackoffDelay(60000, 0));
    }

    // ============ RetryWithBreakerAsync 断路器协同（核心）============

    /// <summary>构造 OpenAiProvider 实例（ModelApiErrorOptions 可配，默认 MaxRetryCount=2/RetryBaseInterval=1000/CircuitBreakerThreshold=5）。</summary>
    private static OpenAiProvider CreateProvider(int maxRetryCount = 2, int retryBaseInterval = 1000, int circuitBreakerThreshold = 5)
    {
        var opts = new AiOptions
        {
            ApiKey = "test-key",
            ModelId = "test-model",
            Endpoint = "",
            ModelApiErrorOptions = new ModelApiErrorOptions
            {
                MaxRetryCount = maxRetryCount,
                RetryBaseInterval = retryBaseInterval,
                CircuitBreakerThreshold = circuitBreakerThreshold
            }
        };
        return new OpenAiProvider(opts, new NullLogger());
    }

    /// <summary>构造 HttpRequestException 带 status（避开 Task.Delay 实际等待：测试用极小 RetryBaseInterval）。</summary>
    private static Exception HttpException(int status) =>
        new HttpRequestException("err", null, (HttpStatusCode)status);

    [Fact]
    public async Task RetryWithBreaker_AlwaysFails503_CountsOnceNotNTimes()
    {
        // 🔴 断路器协同核心：一次逻辑失败（重试 N 次仍失败）→ _consecutiveFailures 仅 +1 而非 +N
        // 默认 MaxRetryCount=2 → 3 次尝试（1 初始 + 2 重试），全失败 → 计数=1（不是 3）
        var provider = CreateProvider(maxRetryCount: 2, retryBaseInterval: 1);  // retryBaseInterval=1 防 Task.Delay 长等待
        int callCount = 0;
        Func<int, CancellationToken, Task<AiResponse>> factory = (_, _) =>
        {
            callCount++;
            throw HttpException(503);
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.RetryWithBreakerAsync(factory, CancellationToken.None));

        Assert.Equal(3, callCount);  // 1 初始 + 2 重试
        Assert.Equal(1, provider.ConsecutiveFailuresForTest);  // 仅 +1，不是 +3
    }

    [Fact]
    public async Task RetryWithBreaker_FirstFailsThenSucceeds_CountsZeroAndReturns()
    {
        // 第1次抛 503（可重试），第2次成功 → _consecutiveFailures=0（成功清零）+ 返回成功响应 + 调用 2 次
        var provider = CreateProvider(maxRetryCount: 2, retryBaseInterval: 1);
        int callCount = 0;
        var success = new AiResponse { Text = "ok", InputTokens = 1, OutputTokens = 1 };
        Func<int, CancellationToken, Task<AiResponse>> factory = (_, _) =>
        {
            callCount++;
            if (callCount == 1) throw HttpException(503);
            return Task.FromResult(success);
        };

        var result = await provider.RetryWithBreakerAsync(factory, CancellationToken.None);

        Assert.Equal(2, callCount);
        Assert.Same(success, result);
        Assert.Equal(0, provider.ConsecutiveFailuresForTest);  // 成功清零
    }

    [Fact]
    public async Task RetryWithBreaker_InvalidOperationException_NotRetriedNotCounted()
    {
        // IOE 不重试（catch 硬拦截）不计数
        var provider = CreateProvider(maxRetryCount: 2, retryBaseInterval: 1);
        int callCount = 0;
        Func<int, CancellationToken, Task<AiResponse>> factory = (_, _) =>
        {
            callCount++;
            throw new InvalidOperationException("sdk validation");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RetryWithBreakerAsync(factory, CancellationToken.None));

        Assert.Equal(1, callCount);  // 不重试
        Assert.Equal(0, provider.ConsecutiveFailuresForTest);  // 不计数
    }

    [Fact]
    public async Task RetryWithBreaker_OperationCanceledException_NotRetriedNotCounted()
    {
        // OCE（ct 取消）不重试不计数
        var provider = CreateProvider(maxRetryCount: 2, retryBaseInterval: 1);
        int callCount = 0;
        Func<int, CancellationToken, Task<AiResponse>> factory = (_, _) =>
        {
            callCount++;
            throw new OperationCanceledException();
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.RetryWithBreakerAsync(factory, CancellationToken.None));

        Assert.Equal(1, callCount);  // 不重试
        Assert.Equal(0, provider.ConsecutiveFailuresForTest);  // 不计数
    }

    [Fact]
    public async Task RetryWithBreaker_NonRetryable401_CountsOnceNotRetried()
    {
        // 401 不可重试 → 不重试 + 计 1 次
        var provider = CreateProvider(maxRetryCount: 2, retryBaseInterval: 1);
        int callCount = 0;
        Func<int, CancellationToken, Task<AiResponse>> factory = (_, _) =>
        {
            callCount++;
            throw HttpException(401);
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.RetryWithBreakerAsync(factory, CancellationToken.None));

        Assert.Equal(1, callCount);  // 不重试（401 不可重试）
        Assert.Equal(1, provider.ConsecutiveFailuresForTest);  // 计 1 次
    }

    [Fact]
    public async Task RetryWithBreaker_BreakerTripped_ThrowsInvalidOperationExceptionNoAttempt()
    {
        // 断路器已触发（_consecutiveFailures >= threshold）→ 直接抛 InvalidOperationException，不调 attemptFactory
        var provider = CreateProvider(maxRetryCount: 2, retryBaseInterval: 1, circuitBreakerThreshold: 2);
        // 先累积 2 次失败达到 threshold
        int callCount = 0;
        Func<int, CancellationToken, Task<AiResponse>> failFactory = (_, _) =>
        {
            callCount++;
            throw HttpException(503);
        };
        // 第1次：失败，计数=1
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.RetryWithBreakerAsync(failFactory, CancellationToken.None));
        Assert.Equal(3, callCount);  // 1 初始 + 2 重试（第1次失败，计数=1）
        Assert.Equal(1, provider.ConsecutiveFailuresForTest);
        // 第2次：再失败，计数=2（达到 threshold=2）
        callCount = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.RetryWithBreakerAsync(failFactory, CancellationToken.None));
        Assert.Equal(3, callCount);
        Assert.Equal(2, provider.ConsecutiveFailuresForTest);  // 达到 threshold

        // 第3次：断路器触发 → 直接抛 IOE，不调 attemptFactory
        callCount = 0;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RetryWithBreakerAsync(failFactory, CancellationToken.None));
        Assert.Equal(0, callCount);  // 不调 attemptFactory
        Assert.Contains("断路器", ex.Message);
        Assert.Equal(2, provider.ConsecutiveFailuresForTest);  // 不再增加
    }

    [Fact]
    public async Task RetryWithBreaker_SuccessAfterFailures_ResetsBreaker()
    {
        // 失败累积后再成功 → _consecutiveFailures 清零
        var provider = CreateProvider(maxRetryCount: 2, retryBaseInterval: 1, circuitBreakerThreshold: 5);
        int callCount = 0;
        // 第1次调用：全失败，计数=1
        Func<int, CancellationToken, Task<AiResponse>> failFactory = (_, _) =>
        {
            callCount++;
            throw HttpException(503);
        };
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.RetryWithBreakerAsync(failFactory, CancellationToken.None));
        Assert.Equal(1, provider.ConsecutiveFailuresForTest);

        // 第2次调用：第1次失败第2次成功 → 计数清零
        callCount = 0;
        var success = new AiResponse { Text = "ok" };
        Func<int, CancellationToken, Task<AiResponse>> mixedFactory = (_, _) =>
        {
            callCount++;
            if (callCount == 1) throw HttpException(503);
            return Task.FromResult(success);
        };
        var result = await provider.RetryWithBreakerAsync(mixedFactory, CancellationToken.None);
        Assert.Same(success, result);
        Assert.Equal(0, provider.ConsecutiveFailuresForTest);  // 成功清零
    }

    [Fact]
    public async Task RetryWithBreaker_CancellationTokenPropagatesDuringRetryDelay()
    {
        // 重试 Task.Delay 期间 ct 取消 → OCE 传播（不计数）
        var provider = CreateProvider(maxRetryCount: 2, retryBaseInterval: 5000);  // 长 baseInterval 让 Task.Delay 等待
        using var cts = new CancellationTokenSource();
        int callCount = 0;
        Func<int, CancellationToken, Task<AiResponse>> factory = (_, _) =>
        {
            callCount++;
            // 第1次抛 503（可重试）→ 进入 Task.Delay(5000)
            if (callCount == 1)
            {
                // 异步取消 ct，让 Task.Delay 抛 OCE
                _ = Task.Run(async () => { await Task.Delay(50); cts.Cancel(); });
                throw HttpException(503);
            }
            return Task.FromResult(new AiResponse { Text = "ok" });
        };

        // Task.Delay(ct) 在 ct 取消时抛 TaskCanceledException（派生自 OCE），用 ThrowsAnyAsync 容许派生类
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RetryWithBreakerAsync(factory, cts.Token));
        Assert.Equal(1, callCount);  // 第1次抛 503 后进入 Task.Delay，ct 取消 → OCE，不再重试
        Assert.Equal(0, provider.ConsecutiveFailuresForTest);  // OCE 不计数
    }

    [Fact]
    public async Task ResetBreaker_ClearsConsecutiveFailures()
    {
        // ResetBreaker 重置 _consecutiveFailures（供 SummarizePhaseGoalsAsync catch 调用防累积）
        var provider = CreateProvider(maxRetryCount: 0, retryBaseInterval: 1, circuitBreakerThreshold: 5);
        // 制造 1 次失败
        Func<int, CancellationToken, Task<AiResponse>> fail = (_, _) => throw HttpException(503);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.RetryWithBreakerAsync(fail, CancellationToken.None));
        Assert.Equal(1, provider.ConsecutiveFailuresForTest);

        provider.ResetBreaker();
        Assert.Equal(0, provider.ConsecutiveFailuresForTest);
    }
}
