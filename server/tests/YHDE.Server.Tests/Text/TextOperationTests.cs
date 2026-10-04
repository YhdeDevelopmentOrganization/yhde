using System.Buffers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using YHDE.Server.Text;

namespace YHDE.Server.Tests.Text;

// The text edit model (text_editing.md): applying, transforming, and the
// JSON form. The convergence property is what makes live co-editing correct.
public sealed class TextOperationTests
{
    private static int[] Cp(string s) => TextOperation.CodePoints(s);
    private static string Str(int[] cps) => TextOperation.FromCodePoints(cps);

    private static TextOperation Op(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return TextOperation.Parse(doc.RootElement, int.MaxValue)!;
    }

    private static string Json(TextOperation op)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) op.WriteTo(w);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [Fact]
    public void Applies_retain_insert_delete()
    {
        Str(Op("""[5, " there", 6]""").Apply(Cp("hello world"))).Should().Be("hello there world");
        Str(Op("""[5, -1, 6]""").Apply(Cp("helloX world"))).Should().Be("hello world");
        Str(Op("""["// ", 11]""").Apply(Cp("hello world"))).Should().Be("// hello world");
    }

    [Fact]
    public void Refuses_an_edit_for_a_different_length()
    {
        var act = () => Op("[3]").Apply(Cp("four"));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Counts_emoji_and_accents_as_single_characters()
    {
        var text = Cp("a😀é");
        text.Should().HaveCount(3);
        Str(Op("""[1, -1, "🙂", 1]""").Apply(text)).Should().Be("a🙂é");
    }

    [Fact]
    public void Concurrent_inserts_at_the_same_place_put_the_first_argument_first()
    {
        var a = Op("""[2, "A", 2]""");
        var b = Op("""[2, "B", 2]""");
        var (a2, b2) = TextOperation.Transform(a, b);
        var s = Cp("xxyy");
        Str(b2.Apply(a.Apply(s))).Should().Be("xxAByy");
        Str(a2.Apply(b.Apply(s))).Should().Be("xxAByy");
    }

    [Fact]
    public void Round_trips_through_json_in_canonical_form()
    {
        Json(Op("""[1, 1, "a", "b", -1, -1, 3]""")).Should().Be("""[2,"ab",-2,3]""");
        // An insert right after a delete is moved in front of it.
        Json(new TextOperation().Retain(1).Delete(2).Insert(Cp("x")).Retain(1)).Should().Be("""[1,"x",-2,1]""");
    }

    [Fact]
    public void Rejects_malformed_json()
    {
        using var doc = JsonDocument.Parse("""[1, true, "x"]""");
        TextOperation.Parse(doc.RootElement, 100).Should().BeNull();
        using var big = JsonDocument.Parse("""["abcdef"]""");
        TextOperation.Parse(big.RootElement, 3).Should().BeNull();
        using var obj = JsonDocument.Parse("""{"t": 1}""");
        TextOperation.Parse(obj.RootElement, 100).Should().BeNull();
    }

    [Fact]
    public void Any_two_concurrent_edits_converge()
    {
        var rng = new Random(20260925);
        for (var round = 0; round < 3000; round++)
        {
            var text = RandomText(rng, rng.Next(0, 40));
            var a = RandomOp(rng, text.Length);
            var b = RandomOp(rng, text.Length);
            var (a2, b2) = TextOperation.Transform(a, b);
            var left = b2.Apply(a.Apply(text));
            var right = a2.Apply(b.Apply(text));
            left.Should().Equal(right, "round {0}: {1} vs {2} on '{3}'", round, Json(a), Json(b), Str(text));
        }
    }

    [Fact]
    public void Three_people_editing_at_once_end_with_the_same_text()
    {
        // The server's rule: each new edit is transformed over those committed
        // since its version, earlier ones first.
        var rng = new Random(7);
        for (var round = 0; round < 1000; round++)
        {
            var text = RandomText(rng, rng.Next(0, 30));
            var edits = Enumerable.Range(0, 3).Select(_ => RandomOp(rng, text.Length)).ToList();
            var log = new List<TextOperation>();
            var server = text;
            foreach (var e in edits)
            {
                var op = e;
                foreach (var committed in log) op = TextOperation.Transform(committed, op).B;
                server = op.Apply(server);
                log.Add(op);
            }
            // Person 2's editor: its own edit is on screen (still unconfirmed)
            // when the other two arrive; each is transformed over it the way
            // the editor does, and the unconfirmed edit over them.
            var pending = edits[2];
            var local = pending.Apply(text);
            foreach (var committed in log.Take(2))
            {
                var (remote, rest) = TextOperation.Transform(committed, pending);
                local = remote.Apply(local);
                pending = rest;
            }
            local.Should().Equal(server, "round {0}", round);
            pending.Apply(log.Take(2).Aggregate(text, (s, op) => op.Apply(s))).Should().Equal(server);
        }
    }

    private static int[] RandomText(Random rng, int n) =>
        Enumerable.Range(0, n).Select(_ => rng.Next(4) == 0 ? 0x1F600 + rng.Next(5) : 'a' + rng.Next(6)).ToArray();

    private static TextOperation RandomOp(Random rng, int length)
    {
        var op = new TextOperation();
        var left = length;
        while (left > 0)
        {
            var n = rng.Next(1, Math.Min(left, 5) + 1);
            switch (rng.Next(3))
            {
                case 0: op.Retain(n); left -= n; break;
                case 1: op.Delete(n); left -= n; break;
                default: op.Insert(RandomText(rng, rng.Next(1, 4))); break;
            }
        }
        if (rng.Next(2) == 0) op.Insert(RandomText(rng, rng.Next(1, 3)));
        return op;
    }
}
