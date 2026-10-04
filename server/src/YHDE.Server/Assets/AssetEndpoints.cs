using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using YHDE.Server.Gateway;

namespace YHDE.Server.Assets;

// The asset plane's bulk transfer (assets.md, network_protocol.md):
// plain HTTP next to the WebSocket, behind the same access key.
//
//   POST  /assets/missing        {"hashes":[...]} -> which of them to upload
//   HEAD  /assets/blobs/{hash}   200 when stored; 404 + Upload-Offset otherwise
//   GET   /assets/blobs/{hash}   the bytes (Range supported, immutable)
//   PATCH /assets/blobs/{hash}   append a chunk: Upload-Offset, Upload-Length
public static class AssetEndpoints
{
    public const string UploadOffsetHeader = "Upload-Offset";
    public const string UploadLengthHeader = "Upload-Length";
    public const long MaxChunkBytes = 64L * 1024 * 1024;
    public const int MaxHashesPerQuery = 10_000;

    public sealed record MissingRequest(List<string>? Hashes);

    public sealed record MissingResponse(List<string> Missing, Dictionary<string, long> Partial);

    public sealed record ChunkResponse(long Offset, bool Complete);

    public static void MapAssetEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/assets").AddEndpointFilter(async (context, next) =>
        {
            var gate = context.HttpContext.RequestServices.GetRequiredService<AccessGate>();
            var grant = await gate.AdmitAsync(context.HttpContext.Request.Headers.Authorization.FirstOrDefault(),
                context.HttpContext.RequestAborted);
            if (grant is null)
                return Results.Problem("Missing or wrong access key or invite code.", statusCode: StatusCodes.Status401Unauthorized);
            return await next(context);
        });

        group.MapPost("/missing", (MissingRequest request, BlobStore blobs) => Missing(request, blobs));
        group.MapMethods("/blobs/{hash}", [HttpMethods.Head], (string hash, BlobStore blobs, HttpContext context) => Head(hash, blobs, context));
        group.MapGet("/blobs/{hash}", (string hash, BlobStore blobs) => Get(hash, blobs));
        group.MapMethods("/blobs/{hash}", [HttpMethods.Patch], (string hash, BlobStore blobs, HttpContext context) => AppendAsync(hash, blobs, context));
    }

    public static IResult Missing(MissingRequest request, BlobStore blobs)
    {
        var hashes = request.Hashes ?? [];
        if (hashes.Count > MaxHashesPerQuery)
            return Results.Problem($"Ask about at most {MaxHashesPerQuery} hashes at once.", statusCode: StatusCodes.Status400BadRequest);
        var missing = new List<string>();
        var partial = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var hash in hashes.Distinct(StringComparer.Ordinal))
        {
            if (!BlobStore.IsValidHash(hash))
                return Results.Problem($"'{hash}' is not a lowercase hex SHA-256.", statusCode: StatusCodes.Status400BadRequest);
            if (blobs.Exists(hash)) continue;
            missing.Add(hash);
            var offset = blobs.PartialLength(hash);
            if (offset > 0) partial[hash] = offset;
        }
        return Results.Ok(new MissingResponse(missing, partial));
    }

    public static IResult Head(string hash, BlobStore blobs, HttpContext context)
    {
        if (!BlobStore.IsValidHash(hash)) return Results.BadRequest();
        var size = blobs.SizeOf(hash);
        if (size is null)
        {
            context.Response.Headers[UploadOffsetHeader] = blobs.PartialLength(hash).ToString();
            return Results.NotFound();
        }
        context.Response.ContentLength = size;
        return Results.Ok();
    }

    public static IResult Get(string hash, BlobStore blobs)
    {
        if (!BlobStore.IsValidHash(hash)) return Results.BadRequest();
        if (!blobs.Exists(hash)) return Results.NotFound();
        // Content-addressed: the bytes behind a name never change.
        return Results.File(blobs.OpenRead(hash), "application/octet-stream",
            entityTag: new EntityTagHeaderValue($"\"{hash}\""), enableRangeProcessing: true);
    }

    public static async Task<IResult> AppendAsync(string hash, BlobStore blobs, HttpContext context)
    {
        if (!BlobStore.IsValidHash(hash))
            return Results.Problem("Not a lowercase hex SHA-256.", statusCode: StatusCodes.Status400BadRequest);
        if (!long.TryParse(context.Request.Headers[UploadOffsetHeader], out var offset) || offset < 0 ||
            !long.TryParse(context.Request.Headers[UploadLengthHeader], out var total) || total < 0)
            return Results.Problem($"{UploadOffsetHeader} and {UploadLengthHeader} are required.", statusCode: StatusCodes.Status400BadRequest);
        if (context.Request.ContentLength is > MaxChunkBytes)
            return Results.Problem($"Send at most {MaxChunkBytes} bytes per request.", statusCode: StatusCodes.Status413PayloadTooLarge);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaxChunkBytes;

        var result = await blobs.AppendAsync(hash, offset, total, context.Request.Body, context.RequestAborted);
        context.Response.Headers[UploadOffsetHeader] = result.Offset.ToString();
        return result.Status switch
        {
            BlobStore.AppendStatus.Accepted => Results.Ok(new ChunkResponse(result.Offset, false)),
            BlobStore.AppendStatus.Completed or BlobStore.AppendStatus.AlreadyStored => Results.Ok(new ChunkResponse(result.Offset, true)),
            BlobStore.AppendStatus.OffsetMismatch => Results.Conflict(new ChunkResponse(result.Offset, false)),
            BlobStore.AppendStatus.Busy => Results.Problem("Another upload of this file is in progress.", statusCode: StatusCodes.Status423Locked),
            BlobStore.AppendStatus.TooLarge => Results.Problem($"Files are limited to {blobs.MaxBlobBytes} bytes.", statusCode: StatusCodes.Status413PayloadTooLarge),
            BlobStore.AppendStatus.HashMismatch => Results.Problem("The uploaded bytes do not match their hash.", statusCode: StatusCodes.Status422UnprocessableEntity),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }
}
