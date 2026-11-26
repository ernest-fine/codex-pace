using System.Runtime.InteropServices;

namespace CodexTicker;

internal static class TrayMeterIcon
{
    public static nint Create(TrayQuotaReading reading, int size)
    {
        if (reading.Amount is not double amount || !double.IsFinite(amount)) return 0;
        size = Math.Clamp(size, 16, 64);
        var pixels = Render(size, Math.Clamp(amount, 0, 1), reading.Marker, reading.Tone);
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = size,
                Height = -size,
                Planes = 1,
                BitCount = 32,
                SizeImage = (uint)pixels.Length
            }
        };
        nint color = CreateDIBSection(0, ref info, 0, out var bits, 0, 0);
        if (color == 0) return 0;

        nint mask = 0;
        try
        {
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            int stride = ((size + 31) / 32) * 4;
            byte[] maskPixels = new byte[stride * size];
            // A one-bit mask keeps transparent pixels transparent on older shell renderers too.
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (pixels[(y * size + x) * 4 + 3] == 0)
                        maskPixels[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
                }
            }
            var maskInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = size,
                    Height = -size,
                    Planes = 1,
                    BitCount = 1,
                    ColorsUsed = 2,
                    SizeImage = (uint)maskPixels.Length
                },
                Color1 = 0x00ffffff
            };
            mask = CreateDIBSection(0, ref maskInfo, 0, out var maskBits, 0, 0);
            if (mask == 0) return 0;
            Marshal.Copy(maskPixels, 0, maskBits, maskPixels.Length);
            var icon = new IconInfo { IsIcon = true, ColorBitmap = color, MaskBitmap = mask };
            return CreateIconIndirect(ref icon);
        }
        finally
        {
            if (mask != 0) DeleteObject(mask);
            DeleteObject(color);
        }
    }

    private static byte[] Render(int size, double amount, double? marker, TrayQuotaTone tone)
    {
        const int samples = 4;
        var color = tone switch
        {
            TrayQuotaTone.WithinTarget => (R: 64, G: 157, B: 106),
            TrayQuotaTone.SlightlyOverTarget => (R: 218, G: 159, B: 55),
            TrayQuotaTone.OverTarget => (R: 220, G: 83, B: 98),
            _ => (R: 132, G: 144, B: 138)
        };
        byte[] pixels = new byte[size * size * 4];
        double left = size * 0.10;
        double right = size * 0.90;
        double top = size * 0.34;
        double bottom = size * 0.74;
        double fillEnd = left + (right - left) * amount;
        double? markerX = marker is double target && double.IsFinite(target)
            ? left + (right - left) * Math.Clamp(target, 0, 1) : null;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double red = 0, green = 0, blue = 0, alpha = 0;
                for (int sy = 0; sy < samples; sy++)
                {
                    for (int sx = 0; sx < samples; sx++)
                    {
                        double px = x + (sx + 0.5) / samples;
                        double py = y + (sy + 0.5) / samples;
                        int r = 0, g = 0, b = 0, a = 0;
                        if (InsideCapsule(px, py, left, top, right, bottom))
                        {
                            (r, g, b, a) = px <= fillEnd
                                ? (color.R, color.G, color.B, 255) : (127, 138, 131, 170);
                        }
                        if (markerX is double tick && py >= size * 0.20 && py <= size * 0.86)
                        {
                            double distance = Math.Abs(px - Math.Clamp(tick, left + size * 0.025, right - size * 0.025));
                            if (distance <= size * 0.075) (r, g, b, a) = (33, 40, 35, 230);
                            if (distance <= size * 0.030) (r, g, b, a) = (246, 250, 246, 255);
                        }
                        // Windows alpha icons use premultiplied BGRA color data.
                        double opacity = a / 255.0;
                        red += r * opacity;
                        green += g * opacity;
                        blue += b * opacity;
                        alpha += a;
                    }
                }
                int offset = (y * size + x) * 4;
                const int count = samples * samples;
                pixels[offset] = (byte)Math.Round(blue / count);
                pixels[offset + 1] = (byte)Math.Round(green / count);
                pixels[offset + 2] = (byte)Math.Round(red / count);
                pixels[offset + 3] = (byte)Math.Round(alpha / count);
            }
        }
        return pixels;
    }

    private static bool InsideCapsule(double x, double y, double left, double top, double right, double bottom)
    {
        double radius = (bottom - top) / 2;
        double centerX = Math.Clamp(x, left + radius, right - radius);
        double centerY = (top + bottom) / 2;
        return (x - centerX) * (x - centerX) + (y - centerY) * (y - centerY) <= radius * radius;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Color0;
        public uint Color1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint CreateIconIndirect(ref IconInfo info);
}
