using FluentAssertions;
using HybridSlicer.Infrastructure.Resin.Analysis;

namespace HybridSlicer.Infrastructure.Tests.Analysis;

public class MaintenanceReminderTests
{
    [Fact]
    public void NewPrinter_AllOk()
    {
        var reminders = MaintenanceReminder.Check(10);
        reminders.Should().HaveCount(4);
        reminders.Should().OnlyContain(r => r.Priority == "ok");
    }

    [Fact]
    public void OldFep_OverdueReminder()
    {
        var reminders = MaintenanceReminder.Check(100, hoursSinceLastFepChange: 100);
        reminders.Should().Contain(r => r.Component == "FEP Film" && r.Priority == "overdue");
    }

    [Fact]
    public void AllComponents_HaveActions()
    {
        var reminders = MaintenanceReminder.Check(50, 50, 15, 1500, 80);
        foreach (var r in reminders)
        {
            r.Action.Should().NotBeNullOrEmpty();
            r.Component.Should().NotBeNullOrEmpty();
        }
    }
}
