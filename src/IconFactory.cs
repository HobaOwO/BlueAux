using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SDColor = System.Drawing.Color;

namespace PhoneSpeaker
{
    // Draws the app icon (a speaker on a blue-violet tile) so we don't need image files.
    static class IconFactory
    {
        const string Glyph = "";
        static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

        public static Bitmap Render(int size, SDColor? dot)
        {
            var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(SDColor.Transparent);

                float inset = size >= 32 ? size / 16f : 0f;
                var tile = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
                using (var path = RoundedRect(tile, tile.Width * 0.26f))
                using (var fill = new System.Drawing.Drawing2D.LinearGradientBrush(new PointF(0, 0), new PointF(size, size), SDColor.FromArgb(0x2F, 0x86, 0xFF), SDColor.FromArgb(0x8B, 0x5C, 0xF6)))
                    g.FillPath(fill, path);

                var family = GlyphFamily();
                if (family != null)
                {
                    using (var glyph = new GraphicsPath())
                    {
                        float em = size * (size <= 24 ? 0.68f : 0.58f);
                        glyph.AddString(Glyph, family, 0, em, PointF.Empty, StringFormat.GenericTypographic);
                        var bounds = glyph.GetBounds();
                        using (var move = new System.Drawing.Drawing2D.Matrix())
                        {
                            move.Translate(size / 2f - (bounds.X + bounds.Width / 2f), size / 2f - (bounds.Y + bounds.Height / 2f));
                            glyph.Transform(move);
                        }
                        g.FillPath(System.Drawing.Brushes.White, glyph);
                    }
                }

                if (dot.HasValue)
                {
                    float d = size * 0.5f;
                    var ring = new RectangleF(size - d, size - d, d, d);
                    using (var ringBrush = new SolidBrush(SDColor.FromArgb(0x1C, 0x1C, 0x1C)))
                        g.FillEllipse(ringBrush, ring);
                    float pad = Math.Max(1.5f, d * 0.17f);
                    ring.Inflate(-pad, -pad);
                    using (var dotBrush = new SolidBrush(dot.Value))
                        g.FillEllipse(dotBrush, ring);
                }
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

        static System.Drawing.FontFamily GlyphFamily()
        {
            foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                try
                {
                    return new System.Drawing.FontFamily(name);
                }
                catch (ArgumentException)
                {
                }
            }
            return null;
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
