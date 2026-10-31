using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;

namespace Jellyfin.Plugin.DailyPicks;

/// <summary>
/// Draws the library tile: today's posters in a grid, darkened, with the title in the middle.
/// Kept in its own class so a server without SkiaSharp only fails here, and the caller can fall back.
/// </summary>
public static class CollageBuilder
{
    private const int Width = 960;
    private const int Height = 540;
    private const int Columns = 5;
    private const int Rows = 2;

    /// <summary>
    /// Builds the collage as a JPEG.
    /// </summary>
    /// <param name="posters">Poster files (repeated if there are fewer than 10).</param>
    /// <param name="title">Text written in the middle.</param>
    /// <param name="outputPath">Where to write the JPEG.</param>
    public static void Build(IReadOnlyList<string> posters, string title, string outputPath)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        // 5 x 2 posters exactly fill a 16:9 tile; each poster is cropped a little to fill its cell.
        var cellWidth = Width / Columns;
        var cellHeight = Height / Rows;
        using var sampling = new SKPaint { IsAntialias = true };
        for (var cell = 0; cell < Columns * Rows; cell++)
        {
            using var poster = SKBitmap.Decode(posters[cell % posters.Count]);
            if (poster is null)
            {
                continue;
            }

            var target = SKRect.Create((cell % Columns) * cellWidth, (cell / Columns) * cellHeight, cellWidth, cellHeight);
            using var image = SKImage.FromBitmap(poster);
            canvas.DrawImage(image, CoverCrop(poster.Width, poster.Height, target), target, new SKSamplingOptions(SKCubicResampler.Mitchell), sampling);
        }

        // Darken towards the middle so the title stands out on any poster.
        using var shade = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(Width / 2f, Height / 2f),
                Width * 0.6f,
                [new SKColor(0, 0, 0, 200), new SKColor(0, 0, 0, 110)],
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(0, 0, Width, Height, shade);

        using var typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, 96);

        // Shrink long titles so they fit with a margin.
        var textWidth = font.MeasureText(title);
        if (textWidth > Width * 0.85f)
        {
            font.Size *= Width * 0.85f / textWidth;
        }

        var baseline = (Height / 2f) - ((font.Metrics.Ascent + font.Metrics.Descent) / 2f);
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 180), IsAntialias = true, ImageFilter = SKImageFilter.CreateBlur(6, 6) };
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawText(title, (Width / 2f) + 3, baseline + 3, SKTextAlign.Center, font, shadow);
        canvas.DrawText(title, Width / 2f, baseline, SKTextAlign.Center, font, text);

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 88);
        using var file = File.Create(outputPath);
        data.SaveTo(file);
    }

    private static SKRect CoverCrop(int imageWidth, int imageHeight, SKRect target)
    {
        // The largest centered part of the image with the cell's aspect ratio.
        var scale = Math.Max(target.Width / imageWidth, target.Height / imageHeight);
        var width = target.Width / scale;
        var height = target.Height / scale;
        return SKRect.Create((imageWidth - width) / 2, (imageHeight - height) / 2, width, height);
    }
}
