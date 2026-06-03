namespace BadmintonDraw.Core;

public static class OfficialDrawRules
{
    private static readonly IReadOnlyDictionary<int, int[]> SeedPositionTables = new Dictionary<int, int[]>
    {
        [2] = [1, 2],
        [4] = [1, 4],
        [8] = [1, 8],
        [16] = [1, 16, 5, 12],
        [32] = [1, 32, 9, 24, 5, 13, 20, 28],
        [64] = [1, 64, 17, 48, 9, 25, 40, 56, 5, 13, 21, 29, 36, 44, 52, 60],
        [128] = [1, 128, 33, 96, 17, 49, 80, 112, 9, 25, 41, 57, 72, 88, 104, 120],
        [256] =
        [
            1, 256, 65, 192, 33, 97, 160, 224,
            17, 49, 81, 113, 144, 176, 208, 240,
            9, 25, 41, 57, 73, 89, 105, 121,
            136, 152, 168, 184, 200, 216, 232, 248
        ]
    };

    public static int GetMaximumSeedCount(int participantCount)
    {
        if (participantCount <= 0)
        {
            return 0;
        }

        var maximum = participantCount switch
        {
            < 16 => 2,
            < 32 => 4,
            < 64 => 8,
            <= 128 => 16,
            _ => 32
        };

        return Math.Min(maximum, participantCount);
    }

    public static IReadOnlyList<int> GetSeedPositionOrder(int slotCount)
    {
        if (slotCount <= 0)
        {
            return [];
        }

        if (SeedPositionTables.TryGetValue(slotCount, out var table))
        {
            return table
                .Where(position => position <= slotCount)
                .Select(position => position - 1)
                .ToList();
        }

        return BuildBalancedPositionOrder(slotCount);
    }

    public static IReadOnlyList<int> GetSeedGroupOrder(int groupCount)
    {
        return BuildBalancedPositionOrder(groupCount);
    }

    public static bool HaveSameUnit(DrawParticipant left, DrawParticipant right)
    {
        var leftUnits = GetUnitKeys(left);
        if (leftUnits.Count == 0)
        {
            return false;
        }

        var rightUnits = GetUnitKeys(right);
        return rightUnits.Count > 0 && leftUnits.Overlaps(rightUnits);
    }

    private static IReadOnlyList<int> BuildBalancedPositionOrder(int slotCount)
    {
        var order = new List<int>(slotCount);
        AddBalancedPositions(order, 0, slotCount - 1);

        for (var position = 0; position < slotCount; position++)
        {
            AddPosition(order, position);
        }

        return order;
    }

    private static void AddBalancedPositions(ICollection<int> order, int first, int last)
    {
        if (first > last)
        {
            return;
        }

        if (order.Count == 0)
        {
            AddPosition(order, first);
            AddPosition(order, last);
        }

        if (last - first <= 1)
        {
            return;
        }

        var middleLeft = (first + last) / 2;
        var middleRight = middleLeft + 1;
        AddPosition(order, middleLeft);
        AddPosition(order, middleRight);
        AddBalancedPositions(order, first, middleLeft);
        AddBalancedPositions(order, middleRight, last);
    }

    private static void AddPosition(ICollection<int> order, int position)
    {
        if (!order.Contains(position))
        {
            order.Add(position);
        }
    }

    private static HashSet<string> GetUnitKeys(DrawParticipant participant)
    {
        var units = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddUnit(participant.TeamName, units);
        AddUnit(participant.PartnerTeamName, units);
        return units;
    }

    private static void AddUnit(string? unit, ISet<string> units)
    {
        if (!string.IsNullOrWhiteSpace(unit))
        {
            units.Add(unit.Trim());
        }
    }
}
