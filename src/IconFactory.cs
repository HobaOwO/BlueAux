using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SDColor = System.Drawing.Color;

namespace BlueAux
{
    // Draws the app icon (an aux plug with sound waves, on a dark tile) so we don't need image files.
    static class IconFactory
    {
        static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

        static readonly SDColor Tile = SDColor.FromArgb(0x1C, 0x1C, 0x1F);
        static readonly SDColor Blue = SDColor.FromArgb(0x3B, 0x9E, 0xFF);

        public static Bitmap Render(int size, SDColor? dot)
        {
            var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(SDColor.Transparent);

                // A flat near-black tile with a faint edge, so it reads on dark and light taskbars alike.
                float inset = size >= 32 ? size / 16f : 0f;
                var tile = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
                using (var path = RoundedRect(tile, tile.Width * 0.24f))
                using (var fill = new SolidBrush(Tile))
                    g.FillPath(fill, path);
                if (size >= 32)
                {
                    var inner = tile;
                    inner.Inflate(-0.5f, -0.5f);
                    using (var edge = RoundedRect(inner, inner.Width * 0.24f))
                    using (var pen = new System.Drawing.Pen(SDColor.FromArgb(0x24, 0xFF, 0xFF, 0xFF), 1f))
                        g.DrawPath(pen, edge);
                }

                float c = size / 2f;
                DrawPlug(g, size, c - size * 0.07f, c + size * 0.07f, 0.74f, SDColor.White, Blue, Tile);
                DrawWaves(g, size, c + size * 0.1f, c - size * 0.1f, Blue);
                DrawDot(g, size, dot, SDColor.FromArgb(0x1C, 0x1C, 0x1C));
            }
            return bitmap;
        }

        // A 3.5 mm aux plug at 45 degrees: a handle, and a tip with the two rings of a TRS jack.
        static void DrawPlug(Graphics g, int size, float cx, float cy, float scale, SDColor handle, SDColor tip, SDColor groove)
        {
            var state = g.Save();
            g.TranslateTransform(cx, cy);
            g.RotateTransform(-45);
            float length = size * 0.72f * scale;
            float handleW = length * 0.36f, handleH = length * 0.42f;
            float neckW = length * 0.22f, neckH = length * 0.07f;
            float shaftW = length * 0.17f, shaftH = length * 0.51f;
            float top = -length / 2;
            using (var tipBrush = new SolidBrush(tip))
            using (var handleBrush = new SolidBrush(handle))
            using (var grooveBrush = new SolidBrush(groove))
            {
                using (var shaft = RoundedRect(new RectangleF(-shaftW / 2, top, shaftW, shaftH + shaftW / 2), shaftW / 2))
                    g.FillPath(tipBrush, shaft);
                float band = Math.Max(1f, length * 0.045f);
                g.FillRectangle(grooveBrush, -shaftW / 2 - 0.5f, top + shaftH * 0.34f, shaftW + 1, band);
                g.FillRectangle(grooveBrush, -shaftW / 2 - 0.5f, top + shaftH * 0.62f, shaftW + 1, band);
                g.FillRectangle(handleBrush, -neckW / 2, top + shaftH, neckW, neckH + 1);
                using (var body = RoundedRect(new RectangleF(-handleW / 2, top + shaftH + neckH, handleW, handleH), handleW * 0.28f))
                    g.FillPath(handleBrush, body);
            }
            g.Restore(state);
        }

        // Two arcs off the tip: the sound leaving the plug without a cable.
        static void DrawWaves(Graphics g, int size, float cx, float cy, SDColor ink)
        {
            float width = Math.Max(1.5f, size * 0.07f);
            using (var pen = new System.Drawing.Pen(ink, width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float r1 = size * 0.16f, r2 = size * 0.28f;
                g.DrawArc(pen, cx - r1, cy - r1, 2 * r1, 2 * r1, -80, 70);
                g.DrawArc(pen, cx - r2, cy - r2, 2 * r2, 2 * r2, -80, 70);
            }
        }

        // The status dot in the bottom-right corner, ringed so it stands out on anything.
        static void DrawDot(Graphics g, int size, SDColor? dot, SDColor ringColor)
        {
            if (!dot.HasValue) return;
            float d = size * 0.48f;
            var ring = new RectangleF(size - d, size - d, d, d);
            using (var ringBrush = new SolidBrush(ringColor))
                g.FillEllipse(ringBrush, ring);
            float pad = Math.Max(1.5f, d * 0.17f);
            ring.Inflate(-pad, -pad);
            using (var dotBrush = new SolidBrush(dot.Value))
                g.FillEllipse(dotBrush, ring);
        }

        // The tray icon as Windows draws its own: just the mark, with no tile behind it.
        public static Bitmap RenderMono(int size, SDColor? dot, bool darkGlyph)
        {
            var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(SDColor.Transparent);
                // The same mark in one colour, a little larger since there's no tile.
                var ink = darkGlyph ? SDColor.FromArgb(0x1C, 0x1C, 0x1C) : SDColor.White;
                var gap = darkGlyph ? SDColor.FromArgb(0xF3, 0xF3, 0xF3) : SDColor.FromArgb(0x1C, 0x1C, 0x1C);
                float c = size / 2f;
                DrawPlug(g, size, c - size * 0.09f, c + size * 0.09f, 0.92f, ink, ink, gap);
                DrawWaves(g, size, c + size * 0.12f, c - size * 0.12f, ink);
                DrawDot(g, size, dot, darkGlyph ? SDColor.FromArgb(0xF3, 0xF3, 0xF3) : SDColor.FromArgb(0x1C, 0x1C, 0x1C));
            }
            return bitmap;
        }

        public static byte[] BuildIco()
        {
            var images = new byte[IcoSizes.Length][];
            for (int i = 0; i < IcoSizes.Length; i++)
            {
                using (var bitmap = Render(IcoSizes[i], null))
                {
                    // PNG only for the big one, as is customary; older readers choke on PNG at small sizes.
                    if (IcoSizes[i] < 256)
                    {
                        images[i] = Dib(bitmap);
                        continue;
                    }
                    using (var stream = new MemoryStream())
                    {
                        bitmap.Save(stream, ImageFormat.Png);
                        images[i] = stream.ToArray();
                    }
                }
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((short)0);
                writer.Write((short)1);
                writer.Write((short)IcoSizes.Length);
                int offset = 6 + 16 * IcoSizes.Length;
                for (int i = 0; i < IcoSizes.Length; i++)
                {
                    int size = IcoSizes[i];
                    writer.Write((byte)(size >= 256 ? 0 : size));
                    writer.Write((byte)(size >= 256 ? 0 : size));
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((short)1);
                    writer.Write((short)32);
                    writer.Write(images[i].Length);
                    writer.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var image in images) writer.Write(image);
                writer.Flush();
                return stream.ToArray();
            }
        }

        // A 32-bit icon image: header, bottom-up BGRA pixels, then an all-clear AND mask (alpha does the work).
        static byte[] Dib(Bitmap bitmap)
        {
            int size = bitmap.Width;
            int maskStride = (size + 31) / 32 * 4;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(40);
                writer.Write(size);
                writer.Write(size * 2);
                writer.Write((short)1);
                writer.Write((short)32);
                writer.Write(0);
                writer.Write(size * size * 4 + maskStride * size);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                for (int y = size - 1; y >= 0; y--)
                {
                    for (int x = 0; x < size; x++)
                    {
                        var c = bitmap.GetPixel(x, y);
                        writer.Write(c.B);
                        writer.Write(c.G);
                        writer.Write(c.R);
                        writer.Write(c.A);
                    }
                }
                writer.Write(new byte[maskStride * size]);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static void SaveIco(string path)
        {
            File.WriteAllBytes(path, BuildIco());
        }

        public static ImageSource WindowIcon()
        {
            var decoder = new IconBitmapDecoder(new MemoryStream(BuildIco()), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
