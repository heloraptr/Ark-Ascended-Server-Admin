using CronExpressionDescriptor;

namespace ArkAscendedServerAdmin.Scheduling;

/// <summary>
/// Friendly text for a cron expression (B3), such as "At 03:00, Monday through Friday", from
/// CronExpressionDescriptor in 24-hour English. The package stays behind this class so the pages never
/// reference it. An expression Cronos does not accept, or one the describer cannot put into words, comes
/// back as the raw expression.
/// </summary>
public static class ScheduleDescriptions
{
    private static readonly Options _options = new() { Use24HourTimeFormat = true, Locale = "en" };

    /// <summary>The friendly text for <paramref name="cron"/>, or the trimmed expression itself when it cannot be described; empty for null.</summary>
    public static string Describe(string? cron)
    {
        if (!ScheduleOccurrences.TryParse(cron, out _))
        {
            return cron?.Trim() ?? string.Empty;
        }

        var expression = cron!.Trim();
        try
        {
            return ExpressionDescriptor.GetDescription(expression, _options);
        }
        catch (FormatException)
        {
            return expression;
        }
    }
}
