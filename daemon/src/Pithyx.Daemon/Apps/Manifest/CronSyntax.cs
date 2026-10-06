namespace Pithyx.Daemon.Apps.Manifest;

/// <summary>
/// The cron syntax of manifest schedules: five fields separated by single spaces (minute, hour, day of month, month,
/// day of week with 0 and 7 for Sunday). A field is a comma list of <c>*</c>, numbers and ranges <c>a-b</c>; <c>*</c>
/// and ranges may take a step <c>/n</c>. No names, no leading zeros.
/// </summary>
public static class CronSyntax
{
    private static readonly (int Min, int Max)[] Fields = [(0, 59), (0, 23), (1, 31), (1, 12), (0, 7)];

    public static bool IsValid(string cron)
    {
        var fields = cron.Split(' ');
        return fields.Length == Fields.Length && fields.Select((field, index) => IsValidField(field, Fields[index])).All(valid => valid);
    }

    private static bool IsValidField(string field, (int Min, int Max) bounds) =>
        field.Length > 0 && field.Split(',').All(item => IsValidItem(item, bounds));

    private static bool IsValidItem(string item, (int Min, int Max) bounds)
    {
        var slash = item.IndexOf('/', StringComparison.Ordinal);
        var range = slash < 0 ? item : item[..slash];
        if (slash >= 0 && !(TryNumber(item[(slash + 1)..], out var step) && step >= 1 && step <= bounds.Max - bounds.Min + 1))
        {
            return false;
        }
        if (range == "*")
        {
            return true;
        }
        var dash = range.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            // A step needs a range to walk: "5/10" is refused.
            return slash < 0 && TryNumber(range, out var value) && value >= bounds.Min && value <= bounds.Max;
        }
        return TryNumber(range[..dash], out var from) && TryNumber(range[(dash + 1)..], out var to)
            && from >= bounds.Min && to <= bounds.Max && from <= to;
    }

    private static bool TryNumber(string text, out int value)
    {
        value = 0;
        if (text.Length is 0 or > 2 || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }
        foreach (var digit in text)
        {
            if (digit is < '0' or > '9')
            {
                return false;
            }
            value = (value * 10) + (digit - '0');
        }
        return true;
    }
}
