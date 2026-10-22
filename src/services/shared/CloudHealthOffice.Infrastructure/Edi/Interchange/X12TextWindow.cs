namespace CloudHealthOffice.Infrastructure.Edi.Interchange;

/// <summary>
/// A forward window over a text stream for reading X12 segment by segment:
/// lookahead, skipping, reading to a terminator and pushing text back, in
/// memory proportional to the longest segment, not the file.
/// </summary>
public sealed class X12TextWindow(TextReader reader, int bufferSize = 64 * 1024)
{
    private char[] _buffer = new char[Math.Max(bufferSize, 512)];
    private int _start;
    private int _end;
    private bool _eof;

    public int Available => _end - _start;

    /// <summary>True when at least <paramref name="count"/> characters are buffered (false only at the end).</summary>
    public async ValueTask<bool> EnsureAsync(int count, CancellationToken ct = default)
    {
        while (Available < count && !_eof)
        {
            ct.ThrowIfCancellationRequested();
            if (_buffer.Length - _end < 4096 || _buffer.Length - _start < count + 4096)
            {
                // Move what is left to the front; grow only when it would still not fit.
                var size = _buffer.Length;
                while (size < count + 4096 || size - Available < 4096) size *= 2;
                var target = size == _buffer.Length ? _buffer : new char[size];
                Array.Copy(_buffer, _start, target, 0, Available);
                _end = Available;
                _start = 0;
                _buffer = target;
            }
            var n = await reader.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), ct);
            if (n == 0) _eof = true;
            else _end += n;
        }
        return Available >= count;
    }

    public async ValueTask<string> PeekAsync(int count, CancellationToken ct = default)
    {
        await EnsureAsync(count, ct);
        return new string(_buffer, _start, Math.Min(count, Available));
    }

    public async ValueTask SkipAsync(int count, CancellationToken ct = default)
    {
        await EnsureAsync(count, ct);
        _start += Math.Min(count, Available);
    }

    /// <summary>Skips characters while <paramref name="skip"/> holds.</summary>
    public async ValueTask SkipWhileAsync(Func<char, bool> skip, CancellationToken ct = default)
    {
        while (true)
        {
            if (Available == 0 && !await EnsureAsync(1, ct)) return;
            while (_start < _end && skip(_buffer[_start])) _start++;
            if (_start < _end) return;
        }
    }

    /// <summary>The text before the next <paramref name="terminator"/>, which is consumed; at the end, what is left (Terminated false), or null when nothing is.</summary>
    public async ValueTask<(string? Text, bool Terminated)> ReadUntilAsync(char terminator, CancellationToken ct = default)
    {
        var scanned = 0;
        while (true)
        {
            var at = Array.IndexOf(_buffer, terminator, _start + scanned, Available - scanned);
            if (at >= 0)
            {
                var text = new string(_buffer, _start, at - _start);
                _start = at + 1;
                return (text, true);
            }
            scanned = Available;
            if (!await EnsureAsync(Available + 1, ct))
            {
                if (Available == 0) return (null, false);
                var rest = new string(_buffer, _start, Available);
                _start = _end;
                return (rest, false);
            }
        }
    }

    /// <summary>Puts <paramref name="text"/> back in front of what is buffered.</summary>
    public void PushBack(string text)
    {
        if (text.Length <= _start)
        {
            _start -= text.Length;
            text.CopyTo(0, _buffer, _start, text.Length);
            return;
        }
        var target = new char[Math.Max(_buffer.Length, Available + text.Length + 4096)];
        text.CopyTo(0, target, 0, text.Length);
        Array.Copy(_buffer, _start, target, text.Length, Available);
        _end = text.Length + Available;
        _start = 0;
        _buffer = target;
    }

    /// <summary>Reads to the end without keeping anything.</summary>
    public async ValueTask DrainAsync(CancellationToken ct = default)
    {
        _start = _end = 0;
        while (!_eof)
        {
            var n = await reader.ReadAsync(_buffer.AsMemory(), ct);
            if (n == 0) _eof = true;
        }
    }
}
