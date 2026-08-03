using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace RelayCove.Server.Data;

internal static class SqliteValueConverters
{
    internal const string UtcDateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    internal static readonly ValueConverter<Guid, string> GuidToString = new(
        value => value.ToString("D").ToLowerInvariant(),
        value => Guid.ParseExact(value, "D"));

    internal static readonly ValueConverter<DateTime, string> UtcDateTimeToString = new(
        value => FormatUtc(value),
        value => ParseUtc(value));

    internal static DateTime NormalizeUtc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Persistent timestamps must use DateTimeKind.Utc.", parameterName);
        }

        return new DateTime(
            value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond),
            DateTimeKind.Utc);
    }

    private static string FormatUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new InvalidOperationException("Persistent timestamps must use DateTimeKind.Utc.");
        }

        if (value.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new InvalidOperationException("Persistent timestamps must use millisecond precision.");
        }

        return value.ToString(UtcDateTimeFormat, CultureInfo.InvariantCulture);
    }

    private static DateTime ParseUtc(string value) => DateTime.SpecifyKind(
        DateTime.ParseExact(value, UtcDateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None),
        DateTimeKind.Utc);
}
