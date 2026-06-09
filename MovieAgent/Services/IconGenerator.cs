using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MovieAgent.Services;

/// <summary>
/// 图标生成工具类 - 从SVG路径生成PNG和ICO文件
/// </summary>
public static class IconGenerator
{
    /// <summary>
    /// 生成应用图标文件
    /// </summary>
    public static void GenerateIcons()
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var pngPath = Path.Combine(baseDir, "logo.png");
            var icoPath = Path.Combine(baseDir, "logo.ico");

            // 如果文件已存在则跳过
            if (File.Exists(pngPath) && File.Exists(icoPath))
                return;

            // 创建logo的矢量图形
            var drawingGroup = CreateLogoDrawing();
            
            // 生成不同尺寸的PNG
            GeneratePng(drawingGroup, pngPath, 256);
            
            // 生成ICO文件
            GenerateIco(drawingGroup, icoPath);
        }
        catch (Exception)
        {
            // 忽略错误，使用默认图标
        }
    }

    private static DrawingGroup CreateLogoDrawing()
    {
        var drawingGroup = new DrawingGroup();
        
        using (var context = drawingGroup.Open())
        {
            // 背景圆形 - 深蓝色
            var backgroundBrush = new SolidColorBrush(Color.FromRgb(26, 26, 46)); // #1A1A2E
            context.DrawEllipse(backgroundBrush, null, new Point(100, 100), 100, 100);

            // 主色画笔 - 白色
            var whiteBrush = Brushes.White;
            var whitePen = new Pen(whiteBrush, 2);

            // 圆角矩形边框（代表屏幕）- 位置调整到中心
            var rectGeometry = new RectangleGeometry(new Rect(25, 40, 150, 120), 8, 8);
            context.DrawGeometry(null, whitePen, rectGeometry);

            // 播放三角形
            var trianglePoints = new PointCollection
            {
                new Point(55, 60),
                new Point(55, 140),
                new Point(115, 100)
            };
            var triangleGeometry = CreatePolygonGeometry(trianglePoints);
            context.DrawGeometry(whiteBrush, null, triangleGeometry);

            // 三条横线（代表电影胶片/列表）
            var linePen = new Pen(whiteBrush, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            
            // 第一条线
            context.DrawLine(linePen, new Point(130, 65), new Point(160, 65));
            // 第二条线
            context.DrawLine(linePen, new Point(130, 100), new Point(155, 100));
            // 第三条线
            context.DrawLine(linePen, new Point(130, 135), new Point(145, 135));
        }

        return drawingGroup;
    }

    private static Geometry CreatePolygonGeometry(PointCollection points)
    {
        if (points.Count < 3)
            return Geometry.Empty;

        var pathFigure = new PathFigure
        {
            StartPoint = points[0],
            IsClosed = true
        };

        for (int i = 1; i < points.Count; i++)
        {
            pathFigure.Segments.Add(new LineSegment(points[i], true));
        }

        return new PathGeometry(new[] { pathFigure });
    }

    private static void GeneratePng(DrawingGroup drawing, string filePath, int size)
    {
        var bounds = new Rect(0, 0, 200, 200);
        
        var drawingImage = new DrawingImage(drawing);
        var image = new System.Windows.Controls.Image { Source = drawingImage };
        image.Arrange(bounds);

        var renderBitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawDrawing(drawing);
        }
        
        renderBitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

        using (var stream = File.Create(filePath))
        {
            encoder.Save(stream);
        }
    }

    private static void GenerateIco(DrawingGroup drawing, string filePath)
    {
        // ICO文件格式支持多个尺寸
        // 这里生成包含256x256和48x48两种尺寸的ICO文件
        
        var sizes = new[] { 256, 48, 32, 16 };
        var pngDataList = new System.Collections.Generic.List<byte[]>();

        foreach (var size in sizes)
        {
            pngDataList.Add(CreatePngBytes(drawing, size));
        }

        // 创建ICO文件
        CreateIcoFile(filePath, pngDataList, sizes);
    }

    private static byte[] CreatePngBytes(DrawingGroup drawing, int size)
    {
        var bounds = new Rect(0, 0, 200, 200);
        
        var renderBitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // 缩放绘制
            var scale = size / 200.0;
            context.PushTransform(new ScaleTransform(scale, scale));
            context.DrawDrawing(drawing);
        }
        
        renderBitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

        using (var stream = new MemoryStream())
        {
            encoder.Save(stream);
            return stream.ToArray();
        }
    }

    private static void CreateIcoFile(string filePath, System.Collections.Generic.List<byte[]> pngDataList, int[] sizes)
    {
        using (var stream = File.Create(filePath))
        using (var writer = new BinaryWriter(stream))
        {
            // ICONDIR header
            writer.Write((short)0);       // Reserved
            writer.Write((short)1);       // Type (1 = ICO)
            writer.Write((short)pngDataList.Count); // Number of images

            // Calculate data offset
            int dataOffset = 6 + pngDataList.Count * 16;

            // ICONDIRENTRY for each image
            var entries = new System.Collections.Generic.List<(int offset, int size)>();
            for (int i = 0; i < pngDataList.Count; i++)
            {
                var pngData = pngDataList[i];
                var size = sizes[i];
                
                writer.Write((byte)(size >= 256 ? 0 : size));  // Width (0 = 256)
                writer.Write((byte)(size >= 256 ? 0 : size));  // Height (0 = 256)
                writer.Write((byte)0);       // Color palette
                writer.Write((byte)0);       // Reserved
                writer.Write((short)1);      // Color planes
                writer.Write((short)32);     // Bits per pixel
                writer.Write(pngData.Length); // Image data size
                writer.Write(dataOffset);    // Image data offset

                entries.Add((dataOffset, pngData.Length));
                dataOffset += pngData.Length;
            }

            // Write PNG data
            foreach (var pngData in pngDataList)
            {
                writer.Write(pngData);
            }
        }
    }
}
