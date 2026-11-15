namespace SpireAutopilot;

public sealed record LegalAction(string Id, string Label, string Kind, object? Details = null);
public sealed record DecisionFrame(string Token, string Scene, object State, IReadOnlyList<LegalAction> Actions, object? StableContext = null, object? FullStableContext = null, object? FullState = null, bool IsCombatDecision = false);
public sealed record RouteStep(int Row, int Column);
public sealed record AgentSessionInfo(string Mode, int TurnCount, int InputBytes, string? RebaseReason = null);
public sealed record ProviderUsage(long? PromptTokens, long? CachedTokens, long? CompletionTokens, long ElapsedMilliseconds, AgentSessionInfo? Session = null);
public sealed record ModelDecision(string ActionId, string Reason, IReadOnlyList<RouteStep>? RoutePlan = null, string? PlanReason = null, string? ReasoningSummary = null, ProviderUsage? Usage = null, string? RouteOptionId = null, string? RunExperienceNote = null);

public static class ExperienceSummaryRules
{
    public const string Instruction = "经验正文使用肯定句，按‘适用条件—具体行动—结果依据’组织。直接给出有效策略，合并重复内容。事实使用实际数值，推导使用条件句。将失利改写为下一局的行动方案。采用正向表达，省去免责声明、能力说明、信息完整性讨论和证据限制段落。只写策略正文。句式示例：生命低于20时优先休息；瞄准与多段箭矢配合形成爆发；先保留药水再进入高压战斗。保留游戏专有名词。";
    private static readonly System.Text.RegularExpressions.Regex Disallowed = new(
        @"不(?!屈|灭|朽|动|息)|没[有能法见做通]|无(?!色|形|尽|限|畏|敌|实体)|未(?!来)|勿|(?<!分|区|级|识|差|告)别(?!名|人|处)|禁止|避免|缺失|缺乏|不足|仅供|仅能|仅可|仅凭|尚待|待验证|无法|免责声明|不能保证|信息有限|记录有限|证据有限|资料有限|\b(?:cannot|can't|not|never|without|insufficient|unknown|unavailable|disclaimer)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    public static string Violation(string? text) => text == null ? "正文" : Disallowed.Match(text).Value;
    public static bool Accepts(string? text) => !string.IsNullOrWhiteSpace(text) && !Disallowed.IsMatch(text);
}
