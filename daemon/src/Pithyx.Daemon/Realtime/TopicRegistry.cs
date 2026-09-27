using System.Collections.Concurrent;

namespace Pithyx.Daemon.Realtime;

/// <summary>All topic definitions. Modules register theirs at startup; more can be added while running.</summary>
/// <remarks>
/// A definition's snapshot delegate must only use a dependency-free state holder (see <c>DiskInventory</c>,
/// <c>SystemInfoSampler</c>), never a service that needs <see cref="TopicHub"/>: the hub depends on this registry,
/// and such a cycle through DI factories is not detected but deadlocks, including the build-time OpenAPI run.
/// </remarks>
public sealed class TopicRegistry(IEnumerable<TopicDefinition> definitions)
{
    private readonly ConcurrentDictionary<string, TopicDefinition> _definitions =
        new(definitions.Select(d => KeyValuePair.Create(d.Pattern, d)), StringComparer.Ordinal);

    public IEnumerable<TopicDefinition> All => _definitions.Values.OrderBy(d => d.Pattern, StringComparer.Ordinal);

    public void Add(TopicDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_definitions.TryAdd(definition.Pattern, definition))
        {
            throw new InvalidOperationException($"Topic {definition.Pattern} is already registered.");
        }
    }

    /// <summary>The definition a concrete topic belongs to, if any.</summary>
    public TopicDefinition? Find(string topic) =>
        _definitions.TryGetValue(topic, out var exact) ? exact : _definitions.Values.FirstOrDefault(d => TopicPattern.Matches(d.Pattern, topic));
}
