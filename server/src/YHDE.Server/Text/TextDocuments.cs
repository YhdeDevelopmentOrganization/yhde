using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using YHDE.Server.Assets;
using YHDE.Server.Domain;
using YHDE.Server.Operations;
using YHDE.Server.Persistence.Repositories;

namespace YHDE.Server.Text;

// Live text files (text_editing.md): scripts, shaders, JSON, … that several
// people type into at once.
//
// A text file starts as a shared file (its bytes are an asset). From then on
// every keystroke travels as an EditText operation against a known version
// (the seq of the last edit its author had seen). The server brings it up to
// date: it transforms the edit over every edit committed since that version,
// checks it fits the current text, and logs the result. Every editor then
// applies the logged edits in log order and ends with the same text.
//
// The current text of recently edited files is kept in memory; it can always
// be rebuilt from the file's bytes and the logged edits.
public sealed class TextDocuments(
    IOperationRepository operations,
    BlobStore blobs,
    ILogger<TextDocuments> logger)
{
    public const int MaxDocumentChars = 2 * 1024 * 1024;
    public const int MaxInsertChars = 512 * 1024;
    public const int MaxHistory = 4000;
    private const int MaxCachedDocuments = 256;

    private sealed class Document
    {
        public long ResetSeq;   // the asset operation that gave the file its current bytes
        public long Seq;        // the last edit applied
        public int[] Text = [];
        public readonly List<(long Seq, TextOperation Op)> History = [];
        public DateTimeOffset Used = DateTimeOffset.UtcNow;
    }

    private readonly ConcurrentDictionary<(Guid Branch, string Path), Document> _docs = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    // Edits and file operations of one branch are committed one at a time, so
    // an edit is always transformed against exactly what precedes it.
    public SemaphoreSlim LockFor(Guid branchId) => _locks.GetOrAdd(branchId, _ => new SemaphoreSlim(1, 1));

    // A file got new bytes (or moved or went away): its text starts over.
    public void Invalidate(Guid branchId, string? path)
    {
        if (!string.IsNullOrEmpty(path)) _docs.TryRemove((branchId, path), out _);
    }

    public sealed record Prepared(OperationSubmission Submission, TextOperation Op, string Path);

    // Brings an EditText submission up to date. Call with LockFor(branch) held;
    // on success commit Prepared.Submission, then call Applied().
    public async Task<(Prepared? Ready, RejectionCode Code, string Reason)> PrepareAsync(
        OperationSubmission submission, Guid branchId, CancellationToken ct)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(submission.Payload);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (null, RejectionCode.InvalidPayload, "Unreadable edit.");
        }
        var path = root.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
        if (!AssetRules.IsValidPath(path) || AssetRules.IsImportedOutput(path))
            return (null, RejectionCode.InvalidPayload, "Not a shareable text file path.");
        if (!root.TryGetProperty("b", out var b) || !b.TryGetInt64(out var baseSeq) || baseSeq < 0)
            return (null, RejectionCode.InvalidPayload, "An edit needs the version it was made on.");
        if (!root.TryGetProperty("t", out var t))
            return (null, RejectionCode.InvalidPayload, "An edit needs its changes.");
        var op = TextOperation.Parse(t, MaxInsertChars);
        if (op is null) return (null, RejectionCode.InvalidPayload, "Unreadable changes.");
        var save = root.TryGetProperty("save", out var sv) && sv.ValueKind == JsonValueKind.True;

        var document = await LoadAsync(branchId, submission.TargetId, path, ct);
        if (document is null) return (null, RejectionCode.InvalidPayload, "That file is not a shared text file.");
        if (baseSeq < document.ResetSeq)
            return (null, RejectionCode.TextOutdated, "The file was replaced since this edit was made.");
        if (baseSeq > document.Seq)
            return (null, RejectionCode.InvalidPayload, "The edit refers to a version that does not exist.");
        var oldest = document.History.Count > 0 ? document.History[0].Seq - 1 : document.Seq;
        if (baseSeq < oldest && baseSeq != document.ResetSeq)
            return (null, RejectionCode.TextOutdated, "The edit is too old to merge.");

        try
        {
            foreach (var (seq, committed) in document.History)
            {
                if (seq <= baseSeq) continue;
                // The already committed edit goes first where both insert at one place.
                op = TextOperation.Transform(committed, op).B;
            }
        }
        catch (InvalidOperationException)
        {
            return (null, RejectionCode.TextOutdated, "The edit does not fit the file's text.");
        }
        if (op.BaseLength != document.Text.Length)
            return (null, RejectionCode.TextOutdated, "The edit does not fit the file's text.");
        if (op.TargetLength > MaxDocumentChars)
            return (null, RejectionCode.InvalidPayload, "The file would become too large to edit live.");

        var buffer = new ArrayBufferWriter<byte>();
        // Relaxed escaping: code keeps its < > & as they are (this is JSON, not HTML).
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("s", path);
            w.WriteNumber("b", document.Seq); // the version the logged edit applies to
            w.WritePropertyName("t");
            op.WriteTo(w);
            if (save) w.WriteBoolean("save", true);
            w.WriteEndObject();
        }
        var canonical = submission with { Payload = Encoding.UTF8.GetString(buffer.WrittenSpan) };
        return (new Prepared(canonical, op, path), default, "");
    }

    public void Applied(Guid branchId, Prepared prepared, long seq)
    {
        if (!_docs.TryGetValue((branchId, prepared.Path), out var document)) return;
        document.Text = prepared.Op.Apply(document.Text);
        document.Seq = seq;
        document.History.Add((seq, prepared.Op));
        if (document.History.Count > MaxHistory) document.History.RemoveRange(0, document.History.Count - MaxHistory);
        document.Used = DateTimeOffset.UtcNow;
    }

    // The text as every editor has it after replaying the log.
    public static int[]? Decode(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Length > 0 && text[0] == '﻿') text = text[1..];
            // Editors show and edit lines without carriage returns.
            return TextOperation.CodePoints(text.Replace("\r\n", "\n"));
        }
        catch (DecoderFallbackException)
        {
            return null; // not UTF-8 text
        }
    }

    private async Task<Document?> LoadAsync(Guid branchId, Guid targetId, string path, CancellationToken ct)
    {
        if (_docs.TryGetValue((branchId, path), out var cached))
        {
            cached.Used = DateTimeOffset.UtcNow;
            return cached;
        }
        var asset = await operations.GetLatestAssetOpAsync(branchId, targetId, ct);
        if (asset is null || asset.Type == OperationType.DeleteAsset) return null;
        using (var payload = JsonDocument.Parse(asset.Payload))
        {
            var root = payload.RootElement;
            if (!root.TryGetProperty("s", out var s) || s.GetString() != path) return null;
            var hash = root.TryGetProperty("h", out var h) ? h.GetString() : null;
            if (hash is null || !BlobStore.IsValidHash(hash) || blobs.SizeOf(hash) is not { } size || size > MaxDocumentChars * 4L) return null;
            byte[] bytes;
            await using (var stream = blobs.OpenRead(hash))
            {
                bytes = new byte[size];
                await stream.ReadExactlyAsync(bytes, ct);
            }
            var text = Decode(bytes);
            if (text is null) return null;
            var document = new Document { ResetSeq = asset.Seq, Seq = asset.Seq, Text = text };
            foreach (var edit in await operations.GetTargetOpsAsync(branchId, targetId, asset.Seq, OperationType.EditText, ct))
            {
                using var p = JsonDocument.Parse(edit.Payload);
                var op = p.RootElement.TryGetProperty("t", out var t) ? TextOperation.Parse(t, int.MaxValue) : null;
                if (op is null || op.BaseLength != document.Text.Length)
                {
                    logger.LogError("Logged edit {Seq} of {Path} does not apply; the file's live text starts over from it", edit.Seq, path);
                    return null;
                }
                document.Text = op.Apply(document.Text);
                document.Seq = edit.Seq;
                document.History.Add((edit.Seq, op));
            }
            if (document.History.Count > MaxHistory) document.History.RemoveRange(0, document.History.Count - MaxHistory);
            if (_docs.Count >= MaxCachedDocuments)
            {
                var oldest = _docs.OrderBy(kv => kv.Value.Used).First().Key;
                _docs.TryRemove(oldest, out _);
            }
            _docs[(branchId, path)] = document;
            return document;
        }
    }
}
