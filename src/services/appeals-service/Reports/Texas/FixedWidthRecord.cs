using System.Text;

namespace AppealsService.Reports.Texas;

/// <summary>One field of an HHSC fixed-width text file: its 1-based start and width, from the layout tab.</summary>
public sealed record FixedWidthField(string Name, int Start, int Width)
{
    public int End => Start + Width - 1;
}

/// <summary>
/// Builds one line of an HHSC fixed-width text file (UMCM 5.24 layouts):
/// each value left-justified and space-filled to its field's width, and a
/// field with nothing to report left as spaces. A value is never cut to fit:
/// a too-long value is the caller's error, because a truncated id or code
/// would be wrong data in a state submission.
/// </summary>
public sealed class FixedWidthRecord
{
    private readonly IReadOnlyList<FixedWidthField> _layout;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public FixedWidthRecord(IReadOnlyList<FixedWidthField> layout) => _layout = layout;

    public FixedWidthRecord Set(string field, string? value)
    {
        var spec = _layout.FirstOrDefault(f => f.Name == field) ?? throw new ArgumentException($"No field {field} in the layout.", nameof(field));
        if (string.IsNullOrEmpty(value)) return this;
        if (value.Length > spec.Width)
            throw new ArgumentException($"{field} is {spec.Width} characters; '{value}' does not fit.", nameof(value));
        if (value.Any(c => c < 0x20 || c > 0x7E))
            throw new ArgumentException($"{field} must be printable ASCII.", nameof(value));
        _values[field] = value;
        return this;
    }

    public override string ToString()
    {
        var line = new StringBuilder(new string(' ', _layout.Max(f => f.End)));
        foreach (var f in _layout)
        {
            if (_values.TryGetValue(f.Name, out var v))
                for (var i = 0; i < v.Length; i++) line[f.Start - 1 + i] = v[i];
        }
        return line.ToString();
    }

    /// <summary>Checks a layout's positions run on from one another with no gap or overlap.</summary>
    public static IReadOnlyList<FixedWidthField> Layout(params FixedWidthField[] fields)
    {
        var next = 1;
        foreach (var f in fields)
        {
            if (f.Start != next) throw new InvalidOperationException($"{f.Name} starts at {f.Start}, expected {next}.");
            next = f.End + 1;
        }
        return fields;
    }
}
