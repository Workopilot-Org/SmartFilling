using SmartFilling.Engine.Models;

namespace SmartFilling.Engine.Tests.Helpers;

public class ScriptBuilder
{
    private readonly List<PhaseItem> _phases = [];
    private readonly List<FieldDefinition> _fields = [];
    private ScriptSettings? _settings;
    private Dictionary<string, object>? _returnData;

    public ScriptBuilder AddPhase(string name, string? type = null, List<PhaseItem>? steps = null,
        string? loopSource = null, int? maxLoopCount = null, string[]? iframe = null,
        string? aiGoal = null, int? maxAiTurns = null,
        int? rowIndexOffset = null, DetectCondition? condition = null, DetectCondition? loopCondition = null,
        string? onError = null)
    {
        _phases.Add(new PhaseNode
        {
            Kind = "phase",
            Name = name,
            Type = type,
            Steps = steps ?? [],
            LoopSource = loopSource,
            MaxLoopCount = maxLoopCount,
            Iframe = iframe,
            AiGoal = aiGoal,
            MaxAiTurns = maxAiTurns,
            RowIndexOffset = rowIndexOffset,
            Condition = condition,
            LoopCondition = loopCondition,
            OnError = onError
        });
        return this;
    }

    public ScriptBuilder AddSettings(ScriptSettings settings)
    {
        _settings = settings;
        return this;
    }

    public ScriptBuilder AddField(string name, string label, string type = "string",
        string? uiComponent = null, bool? required = null, List<string>? options = null,
        string? transform = null, List<FieldDefinition>? fields = null, string? format = null)
    {
        _fields.Add(new FieldDefinition
        {
            Name = name,
            Label = label,
            Type = type,
            UiComponent = uiComponent,
            Required = required,
            Options = options,
            Transform = transform,
            Fields = fields,
            Format = format
        });
        return this;
    }

    public ScriptBuilder WithReturnData(Dictionary<string, object> returnData)
    {
        _returnData = returnData;
        return this;
    }

    public ScriptV2 Build(string scriptId = "test", string name = "Test Script")
    {
        return new ScriptV2
        {
            ScriptId = scriptId,
            Name = name,
            Phases = _phases,
            Fields = _fields,
            Settings = _settings,
            ReturnData = _returnData
        };
    }

    // Static helper methods for creating steps
    public static StepNode Step(string action, string? selector = null, string? value = null,
        string? field = null, string? description = null, string[]? iframe = null,
        object? storeAs = null, bool? skipIfDataEmpty = null, bool? skipIfElementMissing = null,
        bool? pressEnter = null, bool? useLast = null, PreSetup? preSetup = null,
        StepRetry? retry = null, string? onError = null, int? timeout = null,
        int? maxAiTurns = null, DetectCondition? condition = null, StepFallback? fallback = null,
        string? name = null, string? url = null, string? key = null, string? code = null,
        string? filePath = null, string? property = null, string? extractType = null,
        string? regex = null, int? ms = null, DetectCondition? until = null,
        string? captchaType = null, string? imageSelector = null, string? inputSelector = null,
        string? sliderSelector = null, string? targetSelector = null, string? backgroundSelector = null,
        string? direction = null, int? amount = null, int? index = null,
        bool? accept = null, string? dialogPromptText = null, string? matchBy = null,
        string? toPhase = null, string? toStep = null, string? then = null,
        DetectCondition? detect = null, string? message = null,
        int? maxRetries = null, List<StepNode>? cleanupSteps = null)
    {
        return new StepNode
        {
            Kind = "step",
            Action = action,
            Selector = selector,
            Value = value,
            Field = field,
            Description = description,
            Iframe = iframe,
            StoreAs = storeAs,
            SkipIfDataEmpty = skipIfDataEmpty,
            SkipIfElementMissing = skipIfElementMissing,
            PressEnter = pressEnter,
            UseLast = useLast,
            PreSetup = preSetup,
            Retry = retry,
            OnError = onError,
            Timeout = timeout,
            MaxAiTurns = maxAiTurns,
            Condition = condition,
            Fallback = fallback,
            Name = name,
            Url = url,
            Key = key,
            Code = code,
            FilePath = filePath,
            Property = property,
            ExtractType = extractType,
            Regex = regex,
            Ms = ms,
            Until = until,
            CaptchaType = captchaType,
            ImageSelector = imageSelector,
            InputSelector = inputSelector,
            SliderSelector = sliderSelector,
            TargetSelector = targetSelector,
            BackgroundSelector = backgroundSelector,
            Direction = direction,
            Amount = amount,
            Index = index,
            Accept = accept,
            DialogPromptText = dialogPromptText,
            MatchBy = matchBy,
            ToPhase = toPhase,
            ToStep = toStep,
            Then = then,
            Detect = detect,
            Message = message,
            CleanupSteps = cleanupSteps  // T.14：删 MaxRetries（改 RerunOptions）
        };
    }

    public static StepNode CheckStep(DetectCondition detect, string then,
        string? message = null, string? toPhase = null, string? toStep = null,
        int? maxRetries = null, List<StepNode>? cleanupSteps = null)
    {
        return Step("check", detect: detect, then: then, message: message,
            toPhase: toPhase, toStep: toStep, maxRetries: maxRetries, cleanupSteps: cleanupSteps);
    }

    public static DetectCondition Detect(string type,
        string? selector = null, string? value = null, string? field = null,
        string? check = null, string[]? keywords = null,
        string? urlContains = null, int? count = null, string[]? iframe = null,
        List<DetectCondition>? all = null, List<DetectCondition>? any = null,
        DetectCondition? not = null)
    {
        return new DetectCondition
        {
            Type = type,
            Selector = selector,
            Value = value,
            Field = field,
            Check = check,
            Keywords = keywords,
            UrlContains = urlContains,
            Count = count,
            Iframe = iframe,
            All = all,
            Any = any,
            Not = not
        };
    }

    public static DetectCondition DetectAll(params DetectCondition[] conditions) =>
        new() { All = conditions.ToList() };

    public static DetectCondition DetectAny(params DetectCondition[] conditions) =>
        new() { Any = conditions.ToList() };

    public static DetectCondition DetectNot(DetectCondition condition) =>
        new() { Not = condition };
}
