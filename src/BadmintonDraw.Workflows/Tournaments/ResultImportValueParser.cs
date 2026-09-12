using System.Globalization;
using System.Text.RegularExpressions;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

internal static class ResultImportValueParser
{
    internal static bool Blank(WorkspaceRecordCell cell) => cell.Kind is WorkspaceRecordCellKind.Empty or WorkspaceRecordCellKind.Text && string.IsNullOrWhiteSpace(cell.Text);

    internal static bool Coherent(WorkspaceRecordCell cell)
    {
        if (cell.Text is null || cell.ReadError is not null || !Enum.IsDefined(cell.Kind)) return false;
        return cell.Kind switch
        {
            WorkspaceRecordCellKind.Empty => cell.Text.Length == 0,
            WorkspaceRecordCellKind.Text => true,
            WorkspaceRecordCellKind.Number => double.TryParse(cell.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number),
            WorkspaceRecordCellKind.Boolean => bool.TryParse(cell.Text, out _),
            WorkspaceRecordCellKind.DateTime => Timestamp(cell.Text, out _),
            _ => false
        };
    }

    internal static bool Date(WorkspaceRecordCell cell, out DateOnly day)
    {
        day = default;
        if (cell.Kind == WorkspaceRecordCellKind.Text)
            return DateOnly.TryParseExact(cell.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
        if (cell.Kind == WorkspaceRecordCellKind.DateTime && Timestamp(cell.Text, out var date) && date.TimeOfDay == TimeSpan.Zero)
        { day = DateOnly.FromDateTime(date); return true; }
        return false;
    }

    private static bool Timestamp(string text, out DateTime value)
    {
        if (DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)) return true;
        if (DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset))
        { value = offset.DateTime; return true; } // Keep the represented calendar day, without timezone conversion.
        return false;
    }

    internal static bool Duration(WorkspaceRecordCell cell, out int duration)
    {
        duration = 0;
        if (cell.Kind == WorkspaceRecordCellKind.Number)
        {
            if (!double.TryParse(cell.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < 0 || n > int.MaxValue || n != Math.Truncate(n)) return false;
            duration = (int)n; return true;
        }
        if (cell.Kind != WorkspaceRecordCellKind.Text) return false;
        var match = Regex.Match(cell.Text.Trim(), @"^([0-9]+)\s*(?:[mM]|分钟)?$");
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out duration);
    }

    internal static bool Score(string score, bool team, out string canonical, out bool sideA)
    {
        canonical = ""; sideA = false;
        var normalized = score.Trim().Replace('－', '-').Replace('—', '-').Replace('–', '-');
        var games = Regex.Split(normalized, "[,，;；、]");
        if (games.Length < 1 || games.Length > (team ? 1 : 3)) return false;
        var winsA = 0; var winsB = 0; var values = new List<string>();
        foreach (var game in games)
        {
            if (winsA == 2 || winsB == 2) return false;
            var pair = Regex.Match(game.Trim(), @"^([0-9]+)\s*-\s*([0-9]+)$");
            if (!pair.Success || !int.TryParse(pair.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var a) ||
                !int.TryParse(pair.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var b) || a == b) return false;
            if (a > b) winsA++; else winsB++;
            values.Add(a.ToString(CultureInfo.InvariantCulture) + "-" + b.ToString(CultureInfo.InvariantCulture));
        }
        if (games.Length > 1 && Math.Max(winsA, winsB) != 2) return false;
        canonical = string.Join(", ", values); sideA = winsA > winsB; return true;
    }

    internal static bool Equivalent(TournamentMatchResult current, TournamentMatchResult proposed, bool team)
    {
        var score = current.Score;
        if (current.Kind == TournamentResultKind.Played && Score(score, team, out var canonical, out _)) score = canonical;
        else if (current.Kind == TournamentResultKind.Walkover) score = score.Trim();
        return current.Kind == proposed.Kind && score == proposed.Score && current.DurationMinutes == proposed.DurationMinutes && current.ActualPlayedDay == proposed.ActualPlayedDay;
    }
}
