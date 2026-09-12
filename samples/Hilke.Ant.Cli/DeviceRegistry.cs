using System.Globalization;
using Hilke.Ant.Plus;
using Hilke.Ant.Plus.Common;

namespace Hilke.Ant.Cli;

/// <summary>
/// Mutable per-device record. Every field is guarded by the owning <see cref="DeviceRegistry"/>'s
/// lock; background loops mutate via <see cref="DeviceRegistry.WithEntry"/> and the UI reads clones
/// from <see cref="DeviceRegistry.Snapshot"/>.
/// </summary>
public sealed class TrackedDeviceEntry
{
    public AntPlusDeviceId Id { get; set; }
    public string Token { get; set; } = "";
    public string? Alias { get; set; }
    public byte DeviceType { get; set; }
    public string ProfileName { get; set; } = "";
    public bool Connected { get; set; }
    public byte? ChannelNumber { get; set; }
    public AntPlusChannelState State { get; set; } = AntPlusChannelState.Configured;
    public AntPlusChannelTransitionReason? LastTransitionReason { get; set; }

    public int? HeartRate { get; set; }
    public int? PowerWatts { get; set; }
    public double? AveragePower { get; set; }
    public int? Cadence { get; set; }
    public double? SpeedMps { get; set; }
    public string? TrainerStatus { get; set; }
    public int? RrIntervalMs { get; set; }
    public double? LeftTorqueEffectivenessPercent { get; set; }
    public double? RightTorqueEffectivenessPercent { get; set; }
    public double? LeftPedalSmoothnessPercent { get; set; }
    public double? RightPedalSmoothnessPercent { get; set; }
    public double? CombinedPedalSmoothnessPercent { get; set; }

    public BatteryStatus? Battery { get; set; }
    public double? BatteryVolts { get; set; }
    public sbyte? Rssi { get; set; }
    public ManufacturerInfoPage? Manufacturer { get; set; }
    public ProductInfoPage? Product { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>Shallow copy for lock-free rendering.</summary>
    internal TrackedDeviceEntry Clone() => (TrackedDeviceEntry)MemberwiseClone();
}

/// <summary>
/// The thread-safe single source of truth the UI timer renders and background loops update.
/// </summary>
public sealed class DeviceRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TrackedDeviceEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Return the entry for <paramref name="id"/>, creating it (with profile name) on first sight.
    /// Always refreshes <see cref="TrackedDeviceEntry.LastSeen"/>.
    /// </summary>
    public TrackedDeviceEntry GetOrAdd(AntPlusDeviceId id, string profileName)
    {
        lock (_gate)
        {
            string token = TokenForLocked(id);
            if (_entries.TryGetValue(token, out var existing))
            {
                existing.LastSeen = DateTimeOffset.UtcNow;
                return existing;
            }

            var entry = new TrackedDeviceEntry
            {
                Id = id,
                Token = token,
                DeviceType = id.DeviceType,
                ProfileName = profileName,
                State = AntPlusChannelState.Configured,
                LastSeen = DateTimeOffset.UtcNow,
            };
            _entries[token] = entry;
            return entry;
        }
    }

    private string TokenForLocked(AntPlusDeviceId id)
    {
        string baseToken = id.DeviceNumber.ToString(CultureInfo.InvariantCulture);
        if (_entries.TryGetValue(baseToken, out var existing) && existing.DeviceType != id.DeviceType)
            return $"{id.DeviceNumber}.{id.DeviceType}";
        return baseToken;
    }

    /// <summary>Resolve by exact token first, then case-insensitive alias.</summary>
    public bool TryResolve(string tokenOrAlias, out TrackedDeviceEntry entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(tokenOrAlias, out entry!))
                return true;
            foreach (var e in _entries.Values)
            {
                if (e.Alias is { } a && string.Equals(a, tokenOrAlias, StringComparison.OrdinalIgnoreCase))
                {
                    entry = e;
                    return true;
                }
            }
            entry = null!;
            return false;
        }
    }

    public void Remove(string token)
    {
        lock (_gate) { _entries.Remove(token); }
    }

    public void SetAlias(string token, string alias)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(token, out var e))
                e.Alias = alias;
        }
    }

    /// <summary>Run <paramref name="mutate"/> against the canonical entry under the registry lock.</summary>
    public void WithEntry(string token, Action<TrackedDeviceEntry> mutate)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(token, out var e))
                mutate(e);
        }
    }

    /// <summary>A lock-free copy of every entry for rendering.</summary>
    public IReadOnlyList<TrackedDeviceEntry> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<TrackedDeviceEntry>(_entries.Count);
            foreach (var e in _entries.Values)
                list.Add(e.Clone());
            return list;
        }
    }

    /// <summary>Every token plus every alias (for Tab completion).</summary>
    public IReadOnlyList<string> CompletionTokens()
    {
        lock (_gate)
        {
            var list = new List<string>(_entries.Count);
            foreach (var e in _entries.Values)
            {
                list.Add(e.Token);
                if (e.Alias is { } a)
                    list.Add(a);
            }
            return list;
        }
    }
}
