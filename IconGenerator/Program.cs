using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconGenerator
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            var outputPath = @"D:\study\.net core\MovieAgent\MovieAgent\Resources\appicon.ico";
            
            if (File.Exists(outputPath))
            {
                Console.WriteLine("Icon already exists, skipping generation.");
                return;
            }

            var drawingGroup = CreateLogoDrawing();
            GenerateIco(drawingGroup, outputPath);
            
            Console.WriteLine($"Icon generated successfully at {outputPath}");
        }

        private static DrawingGroup CreateLogoDrawing()
        {
            var drawingGroup = new DrawingGroup();
            
            using (var context = drawingGroup.Open())
            {
                var backgroundBrush = new SolidColorBrush(Color.FromRgb(26, 26, 46));
                context.DrawEllipse(backgroundBrush, null, new Point(100, 100), 100, 100);

                var whiteBrush = Brushes.White;
                var whitePen = new Pen(whiteBrush, 2);

                var rectGeometry = new RectangleGeometry(new Rect(25, 40, 150, 120), 8, 8);
                context.DrawGeometry(null, whitePen, rectGeometry);

                var trianglePoints = new PointCollection
                {
                    new Point(55, 60),
                    new Point(55, 140),
                    new Point(115, 100)
                };
                var triangleGeometry = CreatePolygonGeometry(trianglePoints);
                context.DrawGeometry(whiteBrush, null, triangleGeometry);

                var linePen = new Pen(whiteBrush, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                context.DrawLine(linePen, new Point(130, 65), new Point(160, 65));
                context.DrawLine(linePen, new Point(130, 100), new Point(155, 100));
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

        private static void GenerateIco(DrawingGroup drawing, string filePath)
        {
            var sizes = new[] { 256, 48, 32, 16 };
            var pngDataList = new System.Collections.Generic.List<byte[]>();

            foreach (var size in sizes)
            {
                pngDataList.Add(CreatePngBytes(drawing, size));
            }

            using (var stream = File.Create(filePath))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((short)0);
                writer.Write((short)1);
                writer.Write((short)pngDataList.Count);

                int dataOffset = 6 + pngDataList.Count * 16;

                for (int i = 0; i < pngDataList.Count; i++)
                {
                    var pngData = pngDataList[i];
                    var size = sizes[i];

                    writer.Write((byte)(size >= 256 ? 0 : size));
                    writer.Write((byte)(size >= 256 ? 0 : size));
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((short)1);
                    writer.Write((short)32);
                    writer.Write(pngData.Length);
                    writer.Write(dataOffset);

                    dataOffset += pngData.Length;
                }

                foreach (var pngData in pngDataList)
                {
                    writer.Write(pngData);
                }
            }
        }

        private static byte[] CreatePngBytes(DrawingGroup drawing, int size)
        {
            var renderBitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
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
    }
}
