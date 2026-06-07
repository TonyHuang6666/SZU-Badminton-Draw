namespace BadmintonDraw.Core;

public enum CompetitionMode
{
    SinglesKnockout = 1,
    SinglesRoundRobin = 2,
    TeamKnockout = 3,
    TeamRoundRobin = 4
}

public enum EventKind
{
    Singles = 1,
    Doubles = 2,
    Team = 3
}

public enum DrawAlgorithmVersion
{
    PerGroupPowerOfTwo = 1
}

public enum KnockoutGoal
{
    Champion = 1,
    OneQualifierPerGroup = 2
}

public enum PlacementPlayoff
{
    None = 0,
    ThirdPlace = 1,
    ThirdToEighth = 2
}
