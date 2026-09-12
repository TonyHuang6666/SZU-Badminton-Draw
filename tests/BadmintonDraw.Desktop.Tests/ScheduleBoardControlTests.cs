using System.Reflection;
using Avalonia.Controls;
using BadmintonDraw.Desktop.Controls;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class ScheduleBoardControlTests
{
    [Theory]
    [InlineData("ClearHover")]
    [InlineData("ClearDragState")]
    public void EndingHoverOrDragClearsPendingFeedbackAndExplainsActualSaveBehavior(string cleanup)
    {
        var control = new ScheduleBoardControl();
        var feedback = control.FindControl<TextBlock>("Feedback")!;
        feedback.Text = "正在检查目标位置…";

        // Exercise the same cleanup paths used by cell drop/leave and native drag completion.
        typeof(ScheduleBoardControl).GetMethod(cleanup, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, null);

        Assert.DoesNotContain("正在检查", feedback.Text);
        Assert.Contains("自动保存", feedback.Text);
        Assert.Contains("手动和连锁", feedback.Text);
    }
}
