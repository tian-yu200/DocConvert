using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var output = Path.Combine(root, "assets", "ui");
Directory.CreateDirectory(output);

var coral = (Brush)new SolidColorBrush(Color.FromRgb(216, 106, 82));
var coralSoft = (Brush)new SolidColorBrush(Color.FromRgb(249, 226, 218));
var teal = (Brush)new SolidColorBrush(Color.FromRgb(63, 124, 120));
var tealSoft = (Brush)new SolidColorBrush(Color.FromRgb(222, 238, 236));
var ink = (Brush)new SolidColorBrush(Color.FromRgb(49, 65, 82));
var white = Brushes.White;

RenderPng(Path.Combine(output, "conversion-empty.png"), 720, 480, dc =>
{
    DrawRounded(dc, new Rect(82, 122, 210, 252), 18, white, new Pen(new SolidColorBrush(Color.FromRgb(221, 226, 232)), 3));
    DrawRounded(dc, new Rect(120, 78, 210, 252), 18, white, new Pen(coral, 5));
    dc.DrawRectangle(coralSoft, null, new Rect(148, 112, 102, 18));
    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(225, 229, 234)), null, new Rect(148, 151, 142, 11));
    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(225, 229, 234)), null, new Rect(148, 178, 122, 11));
    dc.DrawRoundedRectangle(tealSoft, null, new Rect(158, 216, 132, 76), 8, 8);
    dc.DrawRectangle(teal, null, new Rect(174, 249, 17, 29));
    dc.DrawRectangle(coral, null, new Rect(204, 232, 17, 46));
    dc.DrawRectangle(ink, null, new Rect(234, 257, 17, 21));

    var arrow = new StreamGeometry();
    using (var ctx = arrow.Open())
    {
        ctx.BeginFigure(new Point(337, 206), true, true);
        ctx.LineTo(new Point(411, 206), true, false);
        ctx.LineTo(new Point(411, 177), true, false);
        ctx.LineTo(new Point(464, 224), true, false);
        ctx.LineTo(new Point(411, 271), true, false);
        ctx.LineTo(new Point(411, 242), true, false);
        ctx.LineTo(new Point(337, 242), true, false);
    }
    arrow.Freeze();
    dc.DrawGeometry(coral, null, arrow);

    DrawRounded(dc, new Rect(455, 116, 198, 226), 18, white, new Pen(teal, 5));
    DrawRounded(dc, new Rect(481, 148, 146, 82), 10, coralSoft, null);
    dc.DrawRectangle(coral, null, new Rect(501, 171, 49, 36));
    dc.DrawEllipse(teal, null, new Point(582, 189), 18, 18);
    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(225, 229, 234)), null, new Rect(487, 260, 128, 11));
    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(225, 229, 234)), null, new Rect(487, 287, 104, 11));
    dc.DrawEllipse(coralSoft, null, new Point(621, 91), 30, 30);
    dc.DrawEllipse(tealSoft, null, new Point(91, 88), 20, 20);
});

var iconPng = Path.Combine(output, "docconvert-icon.png");
RenderPng(iconPng, 512, 512, dc =>
{
    DrawRounded(dc, new Rect(36, 36, 440, 440), 92, new SolidColorBrush(Color.FromRgb(250, 247, 245)), null);
    DrawRounded(dc, new Rect(120, 92, 238, 308), 35, white, new Pen(ink, 18));
    var fold = new StreamGeometry();
    using (var ctx = fold.Open())
    {
        ctx.BeginFigure(new Point(286, 92), true, true);
        ctx.LineTo(new Point(358, 164), true, false);
        ctx.LineTo(new Point(286, 164), true, false);
    }
    fold.Freeze();
    dc.DrawGeometry(tealSoft, new Pen(ink, 12), fold);
    dc.DrawRectangle(coral, null, new Rect(162, 207, 150, 22));
    dc.DrawRectangle(teal, null, new Rect(162, 259, 110, 22));
    var arrows = new StreamGeometry();
    using (var ctx = arrows.Open())
    {
        ctx.BeginFigure(new Point(253, 330), true, true);
        ctx.LineTo(new Point(390, 330), true, false);
        ctx.LineTo(new Point(390, 292), true, false);
        ctx.LineTo(new Point(452, 350), true, false);
        ctx.LineTo(new Point(390, 408), true, false);
        ctx.LineTo(new Point(390, 370), true, false);
        ctx.LineTo(new Point(253, 370), true, false);
    }
    arrows.Freeze();
    dc.DrawGeometry(coral, null, arrows);
});

WritePngIcon(iconPng, Path.Combine(output, "DocConvert.ico"));

static void DrawRounded(DrawingContext dc, Rect rect, double radius, Brush fill, Pen? pen) =>
    dc.DrawRoundedRectangle(fill, pen, rect, radius, radius);

static void RenderPng(string path, int width, int height, Action<DrawingContext> draw)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen()) draw(dc);
    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

static void WritePngIcon(string pngPath, string iconPath)
{
    int[] sizes = [16, 24, 32, 48, 64, 128, 256];
    using var sourceStream = File.OpenRead(pngPath);
    var source = BitmapFrame.Create(sourceStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    var frames = sizes.Select(size => EncodePngFrame(source, size)).ToArray();

    using var stream = File.Create(iconPath);
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)frames.Length);

    var offset = 6 + frames.Length * 16;
    for (var index = 0; index < frames.Length; index++)
    {
        var size = sizes[index];
        writer.Write(size == 256 ? (byte)0 : (byte)size);
        writer.Write(size == 256 ? (byte)0 : (byte)size);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(frames[index].Length);
        writer.Write(offset);
        offset += frames[index].Length;
    }

    foreach (var frame in frames) writer.Write(frame);
}

static byte[] EncodePngFrame(BitmapSource source, int size)
{
    var scaled = new TransformedBitmap(
        source,
        new ScaleTransform(size / (double)source.PixelWidth, size / (double)source.PixelHeight));
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(scaled));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}
