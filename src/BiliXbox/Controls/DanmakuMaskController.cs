using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Svg;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Hosting;

namespace BiliXbox.Controls
{
    // The site's SVG alpha describes the region where comments remain visible.
    // Clip only the comment host: the video and its controls are never masked.
    internal sealed class DanmakuMaskController : IDisposable
    {
        private readonly FrameworkElement _host;
        private CompositionGeometricClip _clip;
        private CompositionPathGeometry _path;
        private string _lastSvg;
        private double _width, _height;
        private int _videoWidth, _videoHeight;

        public DanmakuMaskController(FrameworkElement host) { _host = host; }

        public void Apply(string svg, int videoWidth, int videoHeight)
        {
            double width = _host.ActualWidth, height = _host.ActualHeight;
            if (width <= 0 || height <= 0) { Clear(); return; }
            if (_lastSvg == svg && _width == width && _height == height &&
                _videoWidth == videoWidth && _videoHeight == videoHeight) return;
            if (string.IsNullOrWhiteSpace(svg)) { Clear(); return; }
            var parsed = SvgMaskDocument.Parse(svg);
            var device = CanvasDevice.GetSharedDevice();
            var pieces = new List<CanvasGeometry>();
            CanvasGeometry visible = null;
            try
            {
                double aspect = videoWidth > 0 && videoHeight > 0 ? (double)videoWidth / videoHeight : parsed.Width / parsed.Height;
                double displayWidth = Math.Min(width, height * aspect), displayHeight = displayWidth / aspect;
                float left = (float)((width - displayWidth) / 2), top = (float)((height - displayHeight) / 2);
                var viewport = Matrix3x2.CreateTranslation((float)-parsed.X, (float)-parsed.Y)
                    * Matrix3x2.CreateScale((float)(displayWidth / parsed.Width), (float)(displayHeight / parsed.Height))
                    * Matrix3x2.CreateTranslation(left, top);
                using (var document = CanvasSvgDocument.LoadFromXml(device, parsed.Xml))
                {
                    foreach (var item in parsed.Paths)
                    {
                        var element = document.FindElementById(item.Id);
                        using (var attribute = element.GetAttribute("d") as CanvasSvgPathAttribute)
                        {
                            if (attribute == null) throw new InvalidDataException("人物遮罩缺少轮廓路径。");
                            Matrix3x2 transform = Matrix3x2.Identity;
                            foreach (var id in item.TransformIds)
                                transform *= document.FindElementById(id).GetTransformAttribute("transform");
                            using (var source = attribute.CreatePathGeometry(item.EvenOdd
                                ? CanvasFilledRegionDetermination.Alternate : CanvasFilledRegionDetermination.Winding))
                                pieces.Add(source.Transform(transform * viewport));
                        }
                    }
                }
                // Preserve comments in letterbox bars; the mask follows the
                // displayed video rectangle, including portrait video alignment.
                using (var whole = CanvasGeometry.CreateRectangle(device, 0, 0, (float)width, (float)height))
                using (var video = CanvasGeometry.CreateRectangle(device, left, top, (float)displayWidth, (float)displayHeight))
                    visible = whole.CombineWith(video, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
                foreach (var piece in pieces)
                {
                    var next = visible.CombineWith(piece, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                    visible.Dispose(); visible = next;
                }
                var visual = ElementCompositionPreview.GetElementVisual(_host);
                var newPath = visual.Compositor.CreatePathGeometry(new CompositionPath(visible));
                var newClip = visual.Compositor.CreateGeometricClip(newPath);
                visual.Clip = newClip;
                _clip?.Dispose(); _path?.Dispose();
                _clip = newClip; _path = newPath;
                _lastSvg = svg; _width = width; _height = height;
                _videoWidth = videoWidth; _videoHeight = videoHeight;
            }
            finally
            {
                visible?.Dispose();
                foreach (var piece in pieces) piece.Dispose();
            }
        }

        public void Clear()
        {
            try { ElementCompositionPreview.GetElementVisual(_host).Clip = null; }
            catch (Exception) { /* A window can already be tearing down on Unloaded. */ }
            _clip?.Dispose(); _path?.Dispose(); _clip = null; _path = null; _lastSvg = null;
        }
        public void Dispose() => Clear();
    }

    internal sealed class SvgMaskPath
    {
        public string Id;
        public bool EvenOdd;
        // Leaf to root: System.Numerics uses row-vector multiplication.
        public string[] TransformIds;
    }

    internal sealed class SvgMaskDocument
    {
        public string Xml;
        public double X, Y, Width, Height;
        public readonly List<SvgMaskPath> Paths = new List<SvgMaskPath>();

        internal static SvgMaskDocument Parse(string svg)
        {
            if (string.IsNullOrWhiteSpace(svg) || svg.Length > 1024 * 1024)
                throw new InvalidDataException("人物遮罩大小无效。");
            var result = new SvgMaskDocument();
            XDocument input;
            using (var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 }))
                input = XDocument.Load(reader);
            if (input.Root?.Name.LocalName != "svg") throw new InvalidDataException("人物遮罩不是 SVG。");
            var viewBox = ((string)input.Root.Attribute("viewBox") ?? "0 0 320 180")
                .Split(new[] { ' ', ',', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var dimensions = viewBox.Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            if (dimensions.Length != 4 || dimensions.Any(value => double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 16384)
                || dimensions[2] <= 0 || dimensions[3] <= 0) throw new InvalidDataException("人物遮罩坐标无效。");
            result.X = dimensions[0]; result.Y = dimensions[1]; result.Width = dimensions[2]; result.Height = dimensions[3];
            var all = input.Root.DescendantsAndSelf().ToList();
            if (all.Count > 512) throw new InvalidDataException("人物遮罩过于复杂。");
            int counter = 0;
            var ids = new Dictionary<XElement, string>();
            foreach (var node in all)
            {
                string kind = node.Name.LocalName;
                if (kind != "svg" && kind != "g" && kind != "path") throw new InvalidDataException("此人物遮罩包含暂不支持的图形。");
                ids[node] = "mask" + (counter++).ToString(CultureInfo.InvariantCulture);
            }
            XNamespace ns = "http://www.w3.org/2000/svg";
            Func<XElement, XElement> copy = null;
            copy = node =>
            {
                var clean = new XElement(ns + node.Name.LocalName, new XAttribute("id", ids[node]));
                foreach (string attribute in new[] { "d", "transform", "fill-rule", "viewBox" })
                {
                    string value = (string)node.Attribute(attribute);
                    if (value != null) clean.SetAttributeValue(attribute, value);
                }
                if (node.Name.LocalName == "path")
                {
                    string path = (string)node.Attribute("d");
                    if (string.IsNullOrWhiteSpace(path) || path.Length > 300000 || result.Paths.Count >= 128)
                        throw new InvalidDataException("人物遮罩轮廓无效。");
                    string fill = node.AncestorsAndSelf().Select(parent => (string)parent.Attribute("fill-rule")).FirstOrDefault(value => value != null);
                    result.Paths.Add(new SvgMaskPath { Id = ids[node], EvenOdd = fill == "evenodd",
                        TransformIds = node.AncestorsAndSelf().Where(parent => parent.Attribute("transform") != null).Select(parent => ids[parent]).ToArray() });
                }
                foreach (var child in node.Elements()) clean.Add(copy(child));
                return clean;
            };
            var root = copy(input.Root);
            root.SetAttributeValue("width", result.Width.ToString(CultureInfo.InvariantCulture));
            root.SetAttributeValue("height", result.Height.ToString(CultureInfo.InvariantCulture));
            result.Xml = root.ToString(SaveOptions.DisableFormatting);
            return result;
        }
    }
}
