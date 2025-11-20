using System.Buffers.Binary;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DuplicateDetector.Core;

public class PixelHasher
{
    public virtual async Task<PixelHashResult> HashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        using var image = await Image.LoadAsync<Rgba32>(stream, cancellationToken);

        var pixelCount = image.Width * image.Height;
        var data = new byte[pixelCount * 4 + 8];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0, 4), image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), image.Height);

        image.CopyPixelDataTo(data.AsSpan(8));

        var hashBytes = SHA256.HashData(data);
        var hash = Convert.ToHexString(hashBytes);

        return new PixelHashResult(hash, image.Width, image.Height);
    }
}
