using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace HumanLoopBooking.Services;

// Demo renderer for the custom interactive challenge. It deliberately avoids
// exposing the target coordinate in JSON, but it is not meant to be a secret
// image-processing product. A production app can replace this class through
// IChallengeAssetRenderer while keeping the protocol/risk contracts intact.
public sealed class ChallengeAssetRenderer : IChallengeAssetRenderer
{
    public byte[] RenderBackground(ChallengeSession challenge, string? phase)
    {
        var palette = PaletteFor(challenge.Id);
        var canvas = new PixelCanvas(challenge.Width, challenge.Height);
        var active = string.Equals(phase, "active", StringComparison.OrdinalIgnoreCase) &&
            challenge.StartedAt is not null;

        canvas.Clear(palette.Wash);
        canvas.FillVerticalGradient(palette.Light, palette.Wash);
        canvas.DrawGrid(28, palette.Line.WithAlpha(72));
        canvas.FillCircle(58, 48, 31, palette.Teal.WithAlpha(188));
        canvas.FillCircle(306, 44, 34, palette.Gold.WithAlpha(184));
        canvas.FillCircle(286, 136, 28, palette.Rose.WithAlpha(172));
        canvas.StrokeLine(18, 136, 342, 122, 12, palette.Navy.WithAlpha(58));
        canvas.StrokeLine(24, 78, 344, 69, 8, Rgba.White.WithAlpha(112));

        if (active)
        {
            DrawTarget(canvas, challenge.TargetX, challenge.PieceY, palette, strong: true);
        }
        else if (challenge.Variant == ChallengeVariants.ShiftAfterStart)
        {
            // Before pointerdown the user sees only a plausible preview target. The
            // real target appears after the server records the interaction start.
            DrawTarget(canvas, challenge.PreviewTargetX, challenge.PieceY, palette, strong: false);
        }
        else
        {
            DrawPreviewNoise(canvas, challenge, palette);
        }

        return PngEncoder.EncodeRgba(challenge.Width, challenge.Height, canvas.Pixels);
    }

    public string RenderPiece(ChallengeSession challenge)
    {
        var palette = PaletteFor(challenge.Id);
        var path = PuzzlePath(8, 12);

        return $$"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{{challenge.PieceSize + 16}}" height="{{challenge.PieceSize + 22}}" viewBox="0 0 {{challenge.PieceSize + 16}} {{challenge.PieceSize + 22}}">
              <defs>
                <linearGradient id="pieceFill" x1="0" x2="1" y1="0" y2="1">
                  <stop offset="0" stop-color="{{palette.Gold.Hex}}" />
                  <stop offset=".55" stop-color="{{palette.Teal.Hex}}" />
                  <stop offset="1" stop-color="{{palette.Navy.Hex}}" />
                </linearGradient>
                <filter id="pieceShadow" x="-35%" y="-35%" width="170%" height="170%">
                  <feDropShadow dx="0" dy="6" stdDeviation="4" flood-color="#0f2434" flood-opacity=".28" />
                </filter>
              </defs>
              <path d="{{path}}" fill="url(#pieceFill)" stroke="#ffffff" stroke-width="2.5" filter="url(#pieceShadow)" />
              <path d="M17 32 h28" stroke="#ffffff" stroke-width="3" stroke-linecap="round" opacity=".72" />
              <path d="M20 42 h20" stroke="#ffffff" stroke-width="3" stroke-linecap="round" opacity=".52" />
            </svg>
            """;
    }

    private static string PuzzlePath(int x, int y)
    {
        return FormattableString.Invariant(
            $"M{x} {y} h18 c0 -8 12 -8 12 0 h18 v18 c8 0 8 12 0 12 v18 h-48 v-18 c-8 0 -8 -12 0 -12 z");
    }

    private static void DrawTarget(PixelCanvas canvas, int x, int y, Palette palette, bool strong)
    {
        var shadowAlpha = strong ? (byte)52 : (byte)30;
        var fillAlpha = strong ? (byte)228 : (byte)86;
        var strokeAlpha = strong ? (byte)176 : (byte)72;

        canvas.FillPuzzleMask(x + 2, y + 5, palette.Navy.WithAlpha(shadowAlpha));
        canvas.FillPuzzleMask(x, y, Rgba.White.WithAlpha(fillAlpha));
        canvas.StrokePuzzleMask(x, y, palette.Navy.WithAlpha(strokeAlpha));

        if (strong)
        {
            canvas.StrokePuzzleMask(x - 1, y - 1, Rgba.White.WithAlpha(112));
        }
    }

    private static void DrawPreviewNoise(PixelCanvas canvas, ChallengeSession challenge, Palette palette)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"preview:{challenge.Id}"));
        for (var index = 0; index < 4; index++)
        {
            var x = 54 + (bytes[index] % 238);
            var y = 38 + (bytes[index + 4] % 96);
            canvas.FillCircle(x, y, 7 + (bytes[index + 8] % 7), palette.Navy.WithAlpha(20));
        }
    }

    private static Palette PaletteFor(string challengeId)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(challengeId));
        var palettes = new[]
        {
            new Palette(Rgba.FromHex("#e8f3f1"), Rgba.FromHex("#f7fbff"), Rgba.FromHex("#10a68e"), Rgba.FromHex("#f0bb4c"), Rgba.FromHex("#d56a74"), Rgba.FromHex("#153b59"), Rgba.FromHex("#9bb4c4")),
            new Palette(Rgba.FromHex("#eef4fb"), Rgba.FromHex("#fbfaf4"), Rgba.FromHex("#3f8fc5"), Rgba.FromHex("#e3b448"), Rgba.FromHex("#c66889"), Rgba.FromHex("#22344d"), Rgba.FromHex("#adc1c9")),
            new Palette(Rgba.FromHex("#f0f6ed"), Rgba.FromHex("#f8fbff"), Rgba.FromHex("#2ba87f"), Rgba.FromHex("#e4a747"), Rgba.FromHex("#cf6d5f"), Rgba.FromHex("#263d58"), Rgba.FromHex("#a7b8aa"))
        };

        return palettes[bytes[0] % palettes.Length];
    }

    private sealed record Palette(Rgba Light, Rgba Wash, Rgba Teal, Rgba Gold, Rgba Rose, Rgba Navy, Rgba Line);

    private readonly record struct Rgba(byte R, byte G, byte B, byte A)
    {
        public static readonly Rgba White = new(255, 255, 255, 255);

        public string Hex => $"#{R:x2}{G:x2}{B:x2}";

        public Rgba WithAlpha(byte alpha) => this with { A = alpha };

        public static Rgba FromHex(string hex)
        {
            return new Rgba(
                Convert.ToByte(hex[1..3], 16),
                Convert.ToByte(hex[3..5], 16),
                Convert.ToByte(hex[5..7], 16),
                255);
        }
    }

    private sealed class PixelCanvas
    {
        private const int PieceSize = 48;
        private const int KnobRadius = 6;

        private readonly int _width;
        private readonly int _height;

        public PixelCanvas(int width, int height)
        {
            _width = width;
            _height = height;
            Pixels = new byte[width * height * 4];
        }

        public byte[] Pixels { get; }

        public void Clear(Rgba color)
        {
            for (var y = 0; y < _height; y++)
            {
                for (var x = 0; x < _width; x++)
                {
                    PutOpaque(x, y, color);
                }
            }
        }

        public void FillVerticalGradient(Rgba top, Rgba bottom)
        {
            for (var y = 0; y < _height; y++)
            {
                var t = (double)y / Math.Max(1, _height - 1);
                var color = new Rgba(
                    Lerp(top.R, bottom.R, t),
                    Lerp(top.G, bottom.G, t),
                    Lerp(top.B, bottom.B, t),
                    255);

                for (var x = 0; x < _width; x++)
                {
                    PutOpaque(x, y, color);
                }
            }
        }

        public void DrawGrid(int spacing, Rgba color)
        {
            for (var x = spacing; x < _width; x += spacing)
            {
                StrokeLine(x, 0, x, _height, 1, color);
            }

            for (var y = spacing; y < _height; y += spacing)
            {
                StrokeLine(0, y, _width, y, 1, color);
            }
        }

        public void FillCircle(int centerX, int centerY, int radius, Rgba color)
        {
            var radiusSquared = radius * radius;
            for (var y = centerY - radius; y <= centerY + radius; y++)
            {
                for (var x = centerX - radius; x <= centerX + radius; x++)
                {
                    var dx = x - centerX;
                    var dy = y - centerY;
                    if (dx * dx + dy * dy <= radiusSquared)
                    {
                        Blend(x, y, color);
                    }
                }
            }
        }

        public void StrokeLine(int x1, int y1, int x2, int y2, int width, Rgba color)
        {
            var minX = Math.Min(x1, x2) - width;
            var maxX = Math.Max(x1, x2) + width;
            var minY = Math.Min(y1, y2) - width;
            var maxY = Math.Max(y1, y2) + width;
            var half = width / 2.0;

            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    if (DistanceToSegment(x, y, x1, y1, x2, y2) <= half)
                    {
                        Blend(x, y, color);
                    }
                }
            }
        }

        public void FillPuzzleMask(int x, int y, Rgba color)
        {
            for (var py = y - KnobRadius - 1; py <= y + PieceSize + KnobRadius + 1; py++)
            {
                for (var px = x - KnobRadius - 1; px <= x + PieceSize + KnobRadius + 1; px++)
                {
                    if (InsidePuzzle(px, py, x, y))
                    {
                        Blend(px, py, color);
                    }
                }
            }
        }

        public void StrokePuzzleMask(int x, int y, Rgba color)
        {
            for (var py = y - KnobRadius - 2; py <= y + PieceSize + KnobRadius + 2; py++)
            {
                for (var px = x - KnobRadius - 2; px <= x + PieceSize + KnobRadius + 2; px++)
                {
                    if (!InsidePuzzle(px, py, x, y))
                    {
                        continue;
                    }

                    if (!InsidePuzzle(px - 1, py, x, y) ||
                        !InsidePuzzle(px + 1, py, x, y) ||
                        !InsidePuzzle(px, py - 1, x, y) ||
                        !InsidePuzzle(px, py + 1, x, y))
                    {
                        Blend(px, py, color);
                    }
                }
            }
        }

        private static bool InsidePuzzle(int px, int py, int x, int y)
        {
            var lx = px - x;
            var ly = py - y;
            var inBase = lx is >= 0 and <= PieceSize && ly is >= 0 and <= PieceSize;
            var topKnob = DistanceSquared(lx, ly, 24, 0) <= KnobRadius * KnobRadius;
            var rightKnob = DistanceSquared(lx, ly, PieceSize, 24) <= KnobRadius * KnobRadius;
            var leftKnob = DistanceSquared(lx, ly, 0, 24) <= KnobRadius * KnobRadius;

            return inBase || topKnob || rightKnob || leftKnob;
        }

        private void PutOpaque(int x, int y, Rgba color)
        {
            if (x < 0 || x >= _width || y < 0 || y >= _height)
            {
                return;
            }

            var index = ((y * _width) + x) * 4;
            Pixels[index] = color.R;
            Pixels[index + 1] = color.G;
            Pixels[index + 2] = color.B;
            Pixels[index + 3] = 255;
        }

        private void Blend(int x, int y, Rgba color)
        {
            if (x < 0 || x >= _width || y < 0 || y >= _height)
            {
                return;
            }

            var index = ((y * _width) + x) * 4;
            var alpha = color.A / 255.0;
            Pixels[index] = (byte)Math.Round((color.R * alpha) + (Pixels[index] * (1 - alpha)));
            Pixels[index + 1] = (byte)Math.Round((color.G * alpha) + (Pixels[index + 1] * (1 - alpha)));
            Pixels[index + 2] = (byte)Math.Round((color.B * alpha) + (Pixels[index + 2] * (1 - alpha)));
            Pixels[index + 3] = 255;
        }

        private static byte Lerp(byte start, byte end, double t)
        {
            return (byte)Math.Round(start + ((end - start) * t));
        }

        private static int DistanceSquared(int x, int y, int centerX, int centerY)
        {
            var dx = x - centerX;
            var dy = y - centerY;
            return (dx * dx) + (dy * dy);
        }

        private static double DistanceToSegment(double px, double py, double x1, double y1, double x2, double y2)
        {
            var dx = x2 - x1;
            var dy = y2 - y1;
            if (dx == 0 && dy == 0)
            {
                return Math.Sqrt(((px - x1) * (px - x1)) + ((py - y1) * (py - y1)));
            }

            var t = Math.Clamp((((px - x1) * dx) + ((py - y1) * dy)) / ((dx * dx) + (dy * dy)), 0, 1);
            var projectionX = x1 + (t * dx);
            var projectionY = y1 + (t * dy);
            return Math.Sqrt(((px - projectionX) * (px - projectionX)) + ((py - projectionY) * (py - projectionY)));
        }
    }

    private static class PngEncoder
    {
        private static readonly uint[] CrcTable = BuildCrcTable();

        public static byte[] EncodeRgba(int width, int height, byte[] rgba)
        {
            using var stream = new MemoryStream();
            stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);

            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), height);
            ihdr[8] = 8;
            ihdr[9] = 6;
            ihdr[10] = 0;
            ihdr[11] = 0;
            ihdr[12] = 0;
            WriteChunk(stream, "IHDR", ihdr);

            using var raw = new MemoryStream();
            var rowLength = width * 4;
            for (var y = 0; y < height; y++)
            {
                raw.WriteByte(0);
                raw.Write(rgba, y * rowLength, rowLength);
            }

            using var compressed = new MemoryStream();
            raw.Position = 0;
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                raw.CopyTo(zlib);
            }

            WriteChunk(stream, "IDAT", compressed.ToArray());
            WriteChunk(stream, "IEND", ReadOnlySpan<byte>.Empty);
            return stream.ToArray();
        }

        private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            stream.Write(length);

            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes);
            stream.Write(data);

            Span<byte> crcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, Crc(typeBytes, data));
            stream.Write(crcBytes);
        }

        private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            var crc = 0xffffffffu;
            crc = UpdateCrc(crc, type);
            crc = UpdateCrc(crc, data);
            return crc ^ 0xffffffffu;
        }

        private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes)
            {
                crc = CrcTable[(crc ^ b) & 0xff] ^ (crc >> 8);
            }

            return crc;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < table.Length; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) == 1 ? 0xedb88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}
