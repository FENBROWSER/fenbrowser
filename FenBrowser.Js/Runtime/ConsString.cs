namespace FenBrowser.Js.Runtime;

/// <summary>
/// A string built by concatenation, kept as the two halves until someone
/// actually needs the characters.
///
/// `s += x` in a loop is the ordinary way to build a string in JavaScript, and
/// done eagerly it is quadratic: each step copies everything accumulated so
/// far. Building a 300KB payload that way -- which is exactly what a bot check
/// does -- copied about thirteen gigabytes and took seven seconds. Holding the
/// halves and flattening once, at the point the characters are first read,
/// makes the same loop linear.
/// </summary>
internal sealed class ConsString
{
    // Either a string or a ConsString, until Flatten collapses them.
    private object? _left;
    private object? _right;
    private string? _flat;

    internal ConsString(object left, object right, int length)
    {
        _left = left;
        _right = right;
        Length = length;
    }

    /// <summary>Total character count, known without flattening.</summary>
    internal int Length { get; }

    internal static int LengthOf(object part) =>
        part is string s ? s.Length : ((ConsString)part).Length;

    /// <summary>
    /// The characters. Flattens the tree the first time and keeps the result;
    /// the halves are dropped so a long chain does not pin every intermediate.
    /// </summary>
    internal string Flatten()
    {
        if (_flat is { } already)
        {
            return already;
        }

        var buffer = new char[Length];
        var end = Length;

        // Iterative, not recursive: `s += x` a hundred thousand times builds a
        // tree a hundred thousand deep down its left edge, and recursion there
        // overflows the stack before it can produce an answer.
        var pending = new Stack<object>();
        pending.Push(this);
        while (pending.Count > 0)
        {
            var part = pending.Pop();
            switch (part)
            {
                case string text:
                    end -= text.Length;
                    text.CopyTo(0, buffer, end, text.Length);
                    break;

                case ConsString cons when cons._flat is { } flat:
                    end -= flat.Length;
                    flat.CopyTo(0, buffer, end, flat.Length);
                    break;

                case ConsString cons:
                    // Right first: the stack pops it last, and we fill the
                    // buffer from the end backwards.
                    pending.Push(cons._left!);
                    pending.Push(cons._right!);
                    break;
            }
        }

        _flat = new string(buffer);
        _left = null;
        _right = null;
        return _flat;
    }
}
