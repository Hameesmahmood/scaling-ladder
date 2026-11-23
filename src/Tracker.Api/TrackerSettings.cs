namespace Tracker.Api;

/// <summary>
/// Everything that changes between steps of the ladder. Bound from the "Tracker" configuration
/// section, so a step is just a set of environment variables (Tracker__Pooling=true, ...).
/// </summary>
public sealed class TrackerSettings
{
    public string Database { get; set; } = "Host=localhost;Port=5434;Username=tracker;Password=tracker;Database=tracker";

    public string Redis { get; set; } = "localhost:6380";

    /// <summary>Step 2. Off at the baseline: every request opens, and closes, its own connection.</summary>
    public bool Pooling { get; set; }

    /// <summary>The pool's ceiling per instance, when pooling is on.</summary>
    public int MaxPoolSize { get; set; } = 20;
}
