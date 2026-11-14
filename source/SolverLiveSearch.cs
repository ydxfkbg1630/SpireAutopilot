using System.Reflection;

namespace SpireAutopilot;

/// <summary>Bound the copied search profile for interactive streaming sessions.</summary>
internal static class SolverLiveSearch
{
    internal static volatile bool Enabled = true;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public;

    internal static void Apply(object snapshot)
    {
        if (!Enabled) return;
        var type = snapshot.GetType();
        foreach (var name in new[] { "UseBeamWidthPortfolio", "UseNoveltyPortfolio", "UseEarlyTurnExploration" })
            type.GetProperty(name, Instance)!.SetValue(snapshot, false);
        var profileProperty = type.GetProperty("Profile", Instance)!;
        var profile = profileProperty.GetValue(snapshot)!;
        var profileType = profile.GetType();
        // Clone reference records; reflection edits a private boxed copy for value records.
        if (!profileType.IsValueType)
            profile = profileType.GetMethod("<Clone>$", Instance)!.Invoke(profile, null)!;
        Limit("BeamWidth", 64);
        Limit("MaxExpandedNodes", 50_000);
        Limit("MaxCardBranchesPerNode", 36);
        Limit("MaxPileChoiceBranchesPerAction", 24);
        Limit("MaxHandChoiceBranchesPerAction", 24);
        Limit("SoftTimeBudgetMilliseconds", 20_000);
        profileProperty.SetValue(snapshot, profile);
        void Limit(string name, int ceiling)
        {
            var property = profileType.GetProperty(name, Instance)!;
            var value = property.GetValue(profile)!;
            long bounded = Math.Min(Convert.ToInt64(value), ceiling);
            property.SetValue(profile, Convert.ChangeType(bounded, property.PropertyType));
        }
    }
}
