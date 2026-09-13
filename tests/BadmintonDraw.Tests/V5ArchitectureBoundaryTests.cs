using BadmintonDraw.Core;
using BadmintonDraw.Excel;
using BadmintonDraw.Workflows;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class V5ArchitectureBoundaryTests
{
    [Fact]
    public void RetiredV4TypesAreAbsentFromProductionAssemblies()
    {
        AssertTypesAbsent(typeof(DrawService).Assembly,
            "BadmintonDraw.Core.ScheduleService",
            "BadmintonDraw.Core.ScheduleBoard",
            "BadmintonDraw.Core.CrossEventScheduleBoard",
            "BadmintonDraw.Core.CrossEventConflictDetector",
            "BadmintonDraw.Core.ScheduleDependencyBackfill");

        AssertTypesAbsent(typeof(ParticipantExcelReader).Assembly,
            "BadmintonDraw.Excel.TournamentProgressStore",
            "BadmintonDraw.Excel.MatchRecordReader",
            "BadmintonDraw.Excel.CrossEventConflictReportExcelWriter");

        AssertTypesAbsent(typeof(TournamentWorkspaceWorkflow).Assembly,
            "BadmintonDraw.Workflows.TournamentProgressWorkflow",
            "BadmintonDraw.Workflows.CrossEventConflictWorkflow",
            "BadmintonDraw.Workflows.DrawWorkflow",
            "BadmintonDraw.Workflows.ScheduleWorkflow");
    }

    [Fact]
    public void ExcelAssemblyDoesNotReferenceSqlite()
    {
        Assert.DoesNotContain(
            typeof(ParticipantExcelReader).Assembly.GetReferencedAssemblies(),
            reference => reference.Name is "Microsoft.Data.Sqlite" ||
                         reference.Name!.StartsWith("SQLitePCLRaw", StringComparison.Ordinal));
    }

    [Fact]
    public void V5AuthoritiesRemainAvailable()
    {
        Assert.NotNull(typeof(DrawService).Assembly.GetType("BadmintonDraw.Core.Scheduling.TournamentScheduler"));
        Assert.NotNull(typeof(DrawService).Assembly.GetType("BadmintonDraw.Core.Matches.MatchGraph"));
        Assert.NotNull(typeof(TournamentWorkspaceWorkflow).Assembly.GetType("BadmintonDraw.Workflows.Tournaments.OperationalPackageWorkflow"));
        Assert.NotNull(typeof(ParticipantExcelReader).Assembly.GetType("BadmintonDraw.Excel.WorkspaceMatchRecordReader"));
    }

    private static void AssertTypesAbsent(System.Reflection.Assembly assembly, params string[] names)
    {
        foreach (var name in names)
        {
            Assert.Null(assembly.GetType(name));
        }
    }
}
