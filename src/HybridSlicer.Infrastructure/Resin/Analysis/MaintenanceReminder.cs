namespace HybridSlicer.Infrastructure.Resin.Analysis;

/// <summary>
/// Generates maintenance reminders based on printer usage.
/// Tracks: FEP replacement, LCD screen hours, resin vat cleaning,
/// UV LED degradation, and mechanical lubrication.
/// </summary>
public static class MaintenanceReminder
{
    public sealed record Reminder
    {
        public required string Component { get; init; }
        public required float UsageHours { get; init; }
        public required float IntervalHours { get; init; }
        public required float RemainingHours { get; init; }
        public required string Priority { get; init; } // "ok", "soon", "overdue"
        public required string Action { get; init; }
    }

    public static List<Reminder> Check(
        float totalPrintHours,
        float hoursSinceLastFepChange = 0,
        float hoursSinceLastVatClean = 0,
        float lcdTotalHours = 0,
        float hoursSinceLastLubrication = 0)
    {
        var reminders = new List<Reminder>();

        void Add(string component, float usage, float interval, string action)
        {
            float remaining = Math.Max(0, interval - usage);
            string priority = remaining > interval * 0.3f ? "ok" : remaining > 0 ? "soon" : "overdue";
            reminders.Add(new Reminder
            {
                Component = component, UsageHours = usage,
                IntervalHours = interval, RemainingHours = remaining,
                Priority = priority, Action = action,
            });
        }

        Add("FEP Film", hoursSinceLastFepChange, 80, "Replace FEP film — check for cloudiness, scratches, or dents");
        Add("Resin Vat", hoursSinceLastVatClean, 20, "Clean vat — filter resin through paint strainer, check for cured debris");
        Add("LCD Screen", lcdTotalHours, 2000, "LCD screen nearing end of life — watch for uneven exposure or dead pixels");
        Add("Z-Axis Lubrication", hoursSinceLastLubrication, 100, "Lubricate Z-axis lead screw with PTFE grease");

        return reminders;
    }
}
