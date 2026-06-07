namespace BadmintonDraw.Core;

public static class PlacementPlayoffLabels
{
    public const string GroupName = "名次赛";
    public const string ThirdPlacePhase = "3/4名赛";
    public const string ThirdPlaceMatchName = "3/4名赛";
    public const string FifthToEighthSemiPhase = "5-8名半决赛";
    public const string FifthPlacePhase = "5/6名赛";
    public const string FifthPlaceMatchName = "5/6名赛";
    public const string SeventhPlacePhase = "7/8名赛";
    public const string SeventhPlaceMatchName = "7/8名赛";

    public static string FifthToEighthSemiMatchName(int matchNumber)
    {
        return $"{FifthToEighthSemiPhase}第{matchNumber}场";
    }

    public static string WinnerOf(string matchName)
    {
        return $"{matchName}胜者";
    }

    public static string LoserOf(string matchName)
    {
        return $"{matchName}负者";
    }
}
