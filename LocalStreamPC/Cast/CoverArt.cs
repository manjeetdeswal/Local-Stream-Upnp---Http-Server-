using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace LocalStreamPC.Cast
{
    /// <summary>
    /// The cover picture for the music view, plus a soft blurred copy for the background.
    /// Built once per song on a background thread, so the window only has to draw two bitmaps
    /// (a live blur effect over the whole window is slow and leaves artifacts at the edges).
    /// </summary>
    internal sealed class CoverArt : IDisposable
    {
        /// <summary>Largest cover kept, in pixels (the cover is 360 px wide at 100 % scaling, so this covers 200 %).</summary>
        private const int MaxCoverSize = 720;
        private const int BackdropWidth = 128;

        public Bitmap Cover { get; }
        public Bitmap Backdrop { get; }

        private CoverArt(Bitmap cover, Bitmap backdrop) { Cover = cover; Backdrop = backdrop; }

        public static CoverArt? TryCreate(byte[]? data)
        {
            if (data is not { Length: > 0 }) return null;
            try
            {
                using var source = Decode(data);
                if (source == null) return null;

                // Cover: never enlarged (that only adds blur); large pictures are shrunk to MaxCoverSize.
                Bitmap cover;
                double fit = Math.Min(1.0, (double)MaxCoverSize / Math.Max(source.Width, source.Height));
                if (fit < 1.0)
                {
                    using var shrunk = source.Resize(new SKImageInfo(Math.Max(1, (int)Math.Round(source.Width * fit)), Math.Max(1, (int)Math.Round(source.Height * fit)), SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
                    cover = ToAvalonia(shrunk);
                }
                else cover = ToAvalonia(source);

                // Backdrop: a small, heavily blurred copy. Stretched over the window it stays smooth,
                // and Clamp keeps the edges solid instead of fading out.
                int bw = BackdropWidth, bh = Math.Max(1, (int)Math.Round((double)BackdropWidth * source.Height / source.Width));
                using var small = source.Resize(new SKImageInfo(bw, bh, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium);
                using var blurred = new SKBitmap(new SKImageInfo(bw, bh, SKColorType.Bgra8888, SKAlphaType.Premul));
                using (var canvas = new SKCanvas(blurred))
                using (var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(5, 5, SKShaderTileMode.Clamp) })
                {
                    canvas.Clear(SKColors.Black);
                    canvas.DrawBitmap(small, 0, 0, paint);
                }
                return new CoverArt(cover, ToAvalonia(blurred));
            }
            catch
            {
                return null; // not a picture we can read: the placeholder is shown
            }
        }

        /// <summary>
        /// A cast photo, upright (EXIF rotation applied, as phones store it) and no larger than
        /// <paramref name="maxSize"/> pixels on its long side. Null if it isn't a picture.
        /// </summary>
        public static Bitmap? DecodePhoto(byte[] data, int maxSize)
        {
            try
            {
                using var codec = SKCodec.Create(new SKMemoryStream(data));
                if (codec == null) return null;
                float scale = Math.Min(1f, (float)maxSize / Math.Max(codec.Info.Width, codec.Info.Height));
                var size = codec.GetScaledDimensions(scale);
                using var decoded = SKBitmap.Decode(codec, new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (decoded == null) return null;
                using var upright = Server.ThumbnailService.ApplyOrientation(decoded, codec.EncodedOrigin);
                var source = upright ?? decoded;
                if (Math.Max(source.Width, source.Height) > maxSize * 1.05)
                {
                    double fit = (double)maxSize / Math.Max(source.Width, source.Height);
                    using var shrunk = source.Resize(new SKImageInfo((int)(source.Width * fit), (int)(source.Height * fit), SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
                    return ToAvalonia(shrunk);
                }
                return ToAvalonia(source);
            }
            catch { return null; }
        }

        /// <summary>Pixel size of a picture without decoding it (0×0 if unreadable).</summary>
        public static (int Width, int Height) Measure(byte[]? data)
        {
            if (data is not { Length: > 0 }) return (0, 0);
            try
            {
                using var codec = SKCodec.Create(new SKMemoryStream(data));
                return codec == null ? (0, 0) : (codec.Info.Width, codec.Info.Height);
            }
            catch { return (0, 0); }
        }

        private static SKBitmap? Decode(byte[] data)
        {
            using var codec = SKCodec.Create(new SKMemoryStream(data));
            if (codec == null) return null;
            // Big JPEGs can be decoded straight at a smaller size, which is much faster.
            float scale = Math.Min(1f, (float)MaxCoverSize / Math.Max(codec.Info.Width, codec.Info.Height));
            var size = codec.GetScaledDimensions(scale);
            if (size.Width < 1 || size.Height < 1) size = codec.Info.Size;
            var bitmap = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
            if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput) return bitmap;
            bitmap.Dispose();
            return SKBitmap.Decode(data)?.Copy(SKColorType.Bgra8888);
        }

        private static Bitmap ToAvalonia(SKBitmap bitmap)
        {
            var bgra = bitmap.ColorType == SKColorType.Bgra8888 ? bitmap : bitmap.Copy(SKColorType.Bgra8888);
            try
            {
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, bgra.GetPixels(),
                    new PixelSize(bgra.Width, bgra.Height), new Vector(96, 96), bgra.RowBytes);
            }
            finally
            {
                if (!ReferenceEquals(bgra, bitmap)) bgra.Dispose();
            }
        }

        public void Dispose()
        {
            Cover.Dispose();
            Backdrop.Dispose();
        }
    }
}
