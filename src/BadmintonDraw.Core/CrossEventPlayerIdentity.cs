namespace BadmintonDraw.Core;

public sealed record CrossEventPlayerIdentity(string Name, string StudentId = "", bool IsTeam = false)
{
    public string DisplayName => string.IsNullOrWhiteSpace(StudentId)
        ? Name.Trim()
        : $"{Name.Trim()}（{StudentId.Trim()}）";

    public string IdentityKey
    {
        get
        {
            var studentId = Normalize(StudentId);
            if (!IsTeam && !string.IsNullOrWhiteSpace(studentId)) return $"student:{studentId}";
            return $"{(IsTeam ? "team" : "name")}:{Normalize(Name)}";
        }
    }

    public static CrossEventPlayerIdentity FromName(string name, bool isTeam = false) => new(name, "", isTeam);

    private static string Normalize(string value) =>
        string.Concat(value.Trim().Where(character => !char.IsWhiteSpace(character)));
}
