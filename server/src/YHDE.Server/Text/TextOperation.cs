using System.Text;
using System.Text.Json;

namespace YHDE.Server.Text;

// One edit of a text file (text_editing.md): a list of components that walk
// the whole document from start to end.
//
//   retain n   keep the next n characters
//   insert s   insert s here
//   delete n   remove the next n characters
//
// "Characters" are Unicode code points, the unit Godot's String uses, so
// positions mean the same in the editor and on the server. On the wire it is
// a JSON array: a positive number retains, a negative number deletes, a string
// inserts: [4, "hi", -2, 10]. This is the classic operational-transformation
// text model (as in ot.js / Google Docs), with the server as the one place
// that orders and transforms concurrent edits.
public sealed class TextOperation
{
    public enum Kind : byte { Retain, Insert, Delete }

    public readonly record struct Component(Kind Kind, int Count, int[]? Text)
    {
        public int Length => Kind == Kind.Insert ? Text!.Length : Count;
    }

    public const int MaxComponents = 20_000;

    private readonly List<Component> _ops = [];

    public IReadOnlyList<Component> Components => _ops;
    public int BaseLength { get; private set; }   // length of the text it applies to
    public int TargetLength { get; private set; } // length of the text it produces

    public bool IsNoop => _ops.All(c => c.Kind == Kind.Retain);

    public TextOperation Retain(int n)
    {
        if (n <= 0) return this;
        BaseLength += n;
        TargetLength += n;
        if (_ops.Count > 0 && _ops[^1].Kind == Kind.Retain) _ops[^1] = _ops[^1] with { Count = _ops[^1].Count + n };
        else _ops.Add(new Component(Kind.Retain, n, null));
        return this;
    }

    public TextOperation Insert(int[] text)
    {
        if (text.Length == 0) return this;
        TargetLength += text.Length;
        // Canonical form: an insert never directly follows a delete (insert first).
        if (_ops.Count > 0 && _ops[^1].Kind == Kind.Insert)
        {
            _ops[^1] = _ops[^1] with { Text = [.. _ops[^1].Text!, .. text] };
        }
        else if (_ops.Count > 0 && _ops[^1].Kind == Kind.Delete)
        {
            if (_ops.Count > 1 && _ops[^2].Kind == Kind.Insert)
                _ops[^2] = _ops[^2] with { Text = [.. _ops[^2].Text!, .. text] };
            else
                _ops.Insert(_ops.Count - 1, new Component(Kind.Insert, 0, text));
        }
        else
        {
            _ops.Add(new Component(Kind.Insert, 0, text));
        }
        return this;
    }

    public TextOperation Delete(int n)
    {
        if (n <= 0) return this;
        BaseLength += n;
        if (_ops.Count > 0 && _ops[^1].Kind == Kind.Delete) _ops[^1] = _ops[^1] with { Count = _ops[^1].Count + n };
        else _ops.Add(new Component(Kind.Delete, n, null));
        return this;
    }

    public int[] Apply(int[] text)
    {
        if (text.Length != BaseLength)
            throw new InvalidOperationException($"The edit is for a text of {BaseLength} characters, the file has {text.Length}.");
        var result = new List<int>(TargetLength);
        var at = 0;
        foreach (var c in _ops)
        {
            switch (c.Kind)
            {
                case Kind.Retain:
                    result.AddRange(text.AsSpan(at, c.Count));
                    at += c.Count;
                    break;
                case Kind.Insert:
                    result.AddRange(c.Text!);
                    break;
                case Kind.Delete:
                    at += c.Count;
                    break;
            }
        }
        result.AddRange(text.AsSpan(at));
        return [.. result];
    }

    // transform(a, b) = (a', b') with apply(apply(s, a), b') == apply(apply(s, b), a').
    // When both insert at the same place, a's text goes first.
    public static (TextOperation A, TextOperation B) Transform(TextOperation a, TextOperation b)
    {
        if (a.BaseLength != b.BaseLength)
            throw new InvalidOperationException("Both edits must start from the same text.");
        var a2 = new TextOperation();
        var b2 = new TextOperation();
        var ia = new Cursor(a);
        var ib = new Cursor(b);
        while (!ia.Done || !ib.Done)
        {
            if (!ia.Done && ia.Kind == Kind.Insert)
            {
                a2.Insert(ia.Text!);
                b2.Retain(ia.Text!.Length);
                ia.Next();
                continue;
            }
            if (!ib.Done && ib.Kind == Kind.Insert)
            {
                a2.Retain(ib.Text!.Length);
                b2.Insert(ib.Text!);
                ib.Next();
                continue;
            }
            if (ia.Done || ib.Done) throw new InvalidOperationException("The edits do not cover the same text.");
            var n = Math.Min(ia.Remaining, ib.Remaining);
            switch (ia.Kind, ib.Kind)
            {
                case (Kind.Retain, Kind.Retain):
                    a2.Retain(n);
                    b2.Retain(n);
                    break;
                case (Kind.Delete, Kind.Delete):
                    break; // both removed the same characters
                case (Kind.Delete, Kind.Retain):
                    a2.Delete(n);
                    break;
                case (Kind.Retain, Kind.Delete):
                    b2.Delete(n);
                    break;
            }
            ia.Take(n);
            ib.Take(n);
        }
        return (a2, b2);
    }

    // Walks a component list, splitting retains and deletes as needed.
    private sealed class Cursor(TextOperation op)
    {
        private int _index;
        public int Remaining = op._ops.Count > 0 ? op._ops[0].Length : 0;
        public bool Done => _index >= op._ops.Count;
        public Kind Kind => op._ops[_index].Kind;
        public int[]? Text => op._ops[_index].Text;

        public void Next()
        {
            _index++;
            Remaining = Done ? 0 : op._ops[_index].Length;
        }

        public void Take(int n)
        {
            Remaining -= n;
            if (Remaining == 0) Next();
        }
    }

    // JSON

    public static TextOperation? Parse(JsonElement array, int maxInsertChars)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxComponents) return null;
        var op = new TextOperation();
        var inserted = 0;
        foreach (var e in array.EnumerateArray())
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Number when e.TryGetInt32(out var n) && n != 0:
                    if (n > 0) op.Retain(n);
                    else op.Delete(-n);
                    break;
                case JsonValueKind.String:
                    var text = CodePoints(e.GetString() ?? "");
                    inserted += text.Length;
                    if (text.Length == 0 || inserted > maxInsertChars) return null;
                    op.Insert(text);
                    break;
                default:
                    return null;
            }
        }
        return op;
    }

    public void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartArray();
        foreach (var c in _ops)
        {
            switch (c.Kind)
            {
                case Kind.Retain: w.WriteNumberValue(c.Count); break;
                case Kind.Delete: w.WriteNumberValue(-c.Count); break;
                case Kind.Insert: w.WriteStringValue(FromCodePoints(c.Text!)); break;
            }
        }
        w.WriteEndArray();
    }

    public static int[] CodePoints(string s)
    {
        var list = new List<int>(s.Length);
        foreach (var r in s.EnumerateRunes()) list.Add(r.Value);
        return [.. list];
    }

    public static string FromCodePoints(ReadOnlySpan<int> cps)
    {
        var sb = new StringBuilder(cps.Length);
        foreach (var cp in cps) sb.Append(char.ConvertFromUtf32(cp));
        return sb.ToString();
    }
}
