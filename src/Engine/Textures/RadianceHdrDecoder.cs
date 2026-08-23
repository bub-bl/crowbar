using System.Globalization;
using System.Numerics;
using System.Text;

namespace Crowbar.Engine;

internal readonly record struct HdrImageData(int Width, int Height, float[] Pixels);

internal static class RadianceHdrDecoder
{
    public static HdrImageData Decode(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new InvalidDataException("The HDR stream is not readable.");

        var signature = ReadLine(stream);
        if (signature is not "#?RADIANCE" and not "#?RGBE")
            throw new InvalidDataException("The file is not a Radiance RGBE image.");

        var formatFound = false;
        while (true)
        {
            var line = ReadLine(stream);
            if (line.Length == 0)
                break;
            if (line.Equals("FORMAT=32-bit_rle_rgbe", StringComparison.Ordinal))
                formatFound = true;
        }
        if (!formatFound)
            throw new InvalidDataException("The HDR header does not declare FORMAT=32-bit_rle_rgbe.");

        var resolution = ReadLine(stream).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (resolution.Length != 4 ||
            !TryAxis(resolution[0], resolution[1], 'Y', out var ySign, out var height) ||
            !TryAxis(resolution[2], resolution[3], 'X', out var xSign, out var width))
        {
            throw new InvalidDataException("The HDR resolution line is malformed.");
        }
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("The HDR dimensions must be positive.");

        var pixels = new float[checked(width * height * 4)];
        var scanline = new byte[checked(width * 4)];
        for (var sourceY = 0; sourceY < height; sourceY++)
        {
            DecodeScanline(stream, width, scanline);
            var targetY = ySign == '-' ? sourceY : height - 1 - sourceY;
            for (var sourceX = 0; sourceX < width; sourceX++)
            {
                var targetX = xSign == '+' ? sourceX : width - 1 - sourceX;
                var source = sourceX * 4;
                var target = (targetY * width + targetX) * 4;
                var exponent = scanline[source + 3];
                if (exponent == 0)
                {
                    pixels[target + 3] = 1f;
                    continue;
                }

                var scale = MathF.Pow(2f, exponent - 136f);
                pixels[target] = scanline[source] * scale;
                pixels[target + 1] = scanline[source + 1] * scale;
                pixels[target + 2] = scanline[source + 2] * scale;
                pixels[target + 3] = 1f;
            }
        }

        return new HdrImageData(width, height, pixels);
    }

    private static void DecodeScanline(Stream stream, int width, Span<byte> target)
    {
        Span<byte> header = stackalloc byte[4];
        ReadExactly(stream, header);
        var encodedWidth = (header[2] << 8) | header[3];
        if (width < 8 || width > 32767 || header[0] != 2 || header[1] != 2 || encodedWidth != width)
        {
            header.CopyTo(target);
            ReadExactly(stream, target[4..]);
            return;
        }

        for (var channel = 0; channel < 4; channel++)
        {
            var x = 0;
            while (x < width)
            {
                var count = stream.ReadByte();
                if (count < 0)
                    throw new EndOfStreamException("Unexpected end of HDR scanline.");

                if (count > 128)
                {
                    var run = count - 128;
                    var value = stream.ReadByte();
                    if (run == 0 || value < 0 || x + run > width)
                        throw new InvalidDataException("The HDR RLE run is malformed.");
                    for (var index = 0; index < run; index++)
                        target[(x++ * 4) + channel] = (byte)value;
                }
                else
                {
                    if (count == 0 || x + count > width)
                        throw new InvalidDataException("The HDR literal run is malformed.");
                    for (var index = 0; index < count; index++)
                    {
                        var value = stream.ReadByte();
                        if (value < 0)
                            throw new EndOfStreamException("Unexpected end of HDR scanline.");
                        target[(x++ * 4) + channel] = (byte)value;
                    }
                }
            }
        }
    }

    private static bool TryAxis(
        string axisToken,
        string sizeToken,
        char expectedAxis,
        out char sign,
        out int size)
    {
        sign = axisToken.Length == 2 ? axisToken[0] : '\0';
        size = 0;
        return axisToken.Length == 2 &&
               (sign == '+' || sign == '-') &&
               axisToken[1] == expectedAxis &&
               int.TryParse(sizeToken, NumberStyles.None, CultureInfo.InvariantCulture, out size);
    }

    private static string ReadLine(Stream stream)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var value = stream.ReadByte();
            if (value < 0)
            {
                if (bytes.Count == 0)
                    throw new EndOfStreamException("Unexpected end of HDR header.");
                break;
            }
            if (value == '\n')
                break;
            if (value != '\r')
                bytes.Add((byte)value);
        }
        return Encoding.ASCII.GetString([.. bytes]);
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var count = stream.Read(destination[offset..]);
            if (count == 0)
                throw new EndOfStreamException("Unexpected end of HDR pixel data.");
            offset += count;
        }
    }
}
