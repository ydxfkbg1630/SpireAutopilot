namespace SpireAutopilot;

public sealed record LegalAction(string Id, string Label, string Kind, object? Details = null);
public sealed record DecisionFrame(string Token, string Scene, object State, IReadOnlyList<LegalAction> Actions, object? StableContext = null, object? FullStableContext = null, object? FullState = null, bool IsCombatDecision = false);
public sealed record RouteStep(int Row, int Column);
public sealed record AgentSessionInfo(string Mode, int TurnCount, int InputBytes, string? RebaseReason = null);
public sealed record ProviderUsage(long? PromptTokens, long? CachedTokens, long? CompletionTokens, long ElapsedMilliseconds, AgentSessionInfo? Session = null);
public sealed record ModelDecision(string ActionId, string Reason, IReadOnlyList<RouteStep>? RoutePlan = null, string? PlanReason = null, string? ReasoningSummary = null, ProviderUsage? Usage = null, string? RouteOptionId = null, string? RunExperienceNote = null);
