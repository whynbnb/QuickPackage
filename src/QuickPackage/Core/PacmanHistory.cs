using System.Globalization;
using System.Text.RegularExpressions;

namespace QuickPackage.Core;

public enum HistoryAction
{
    Installed,
    Upgraded,
    Removed,
    Reinstalled,
    Downgraded,
}

public sealed record HistoryEntry(
    DateTimeOffset Time,
    HistoryAction Action,
    string Name,
    string OldVersion,
    string NewVersion);

/// <summary>Reads the libalpm transaction lines from /var/log/pacman.log.</summary>
public static partial class PacmanHistory
{
    private const string LogPath = "/var/log/pacman.log";

    public static List<HistoryEntry> Load(int limit = 5000)
    {
        var entries = new List<HistoryEntry>();
        try
        {
            if (!File.Exists(LogPath))
                return entries;

            foreach (var rawLine in File.ReadLines(LogPath))
            {
                var match = EntryRegex().Match(rawLine);
                if (!match.Success)
                    continue;
                if (!DateTimeOffset.TryParse(
                        match.Groups["time"].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal, out var time))
                    continue;

                var action = match.Groups["action"].Value switch
                {
                    "installed" => HistoryAction.Installed,
                    "upgraded" => HistoryAction.Upgraded,
                    "removed" => HistoryAction.Removed,
                    "reinstalled" => HistoryAction.Reinstalled,
                    "downgraded" => HistoryAction.Downgraded,
                    _ => (HistoryAction?)null,
                };
                if (action is null)
                    continue;

                var detail = match.Groups["detail"].Value;
                var arrow = detail.IndexOf(" -> ", StringComparison.Ordinal);
                var oldVersion = arrow < 0
                    ? (action == HistoryAction.Installed ? string.Empty : detail)
                    : detail[..arrow];
                var newVersion = arrow < 0 ? detail : detail[(arrow + 4)..];

                entries.Add(new HistoryEntry(
                    time, action.Value, match.Groups["name"].Value, oldVersion, newVersion));
            }
        }
        catch
        {
            // An unreadable log simply yields no history.
        }

        if (entries.Count > limit)
            entries.RemoveRange(0, entries.Count - limit);
        entries.Reverse();
        return entries;
    }

    [GeneratedRegex(
        @"^\[(?<time>[^\]]+)\] \[ALPM\] (?<action>installed|upgraded|removed|reinstalled|downgraded) (?<name>\S+) \((?<detail>.*)\)$")]
    private static partial Regex EntryRegex();
}
