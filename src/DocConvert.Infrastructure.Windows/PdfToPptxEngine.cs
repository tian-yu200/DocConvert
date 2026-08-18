using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using DocConvert.Core;

namespace DocConvert.Infrastructure.Windows;

public sealed class PdfToPptxEngine : IConversionEngine
{
    private const long SlideWidth = 12192000;
    private const long SlideHeight = 6858000;

    public string Name => "PDF 转 PowerPoint 引擎";

    public bool CanHandle(DocumentJobRequest request) =>
        request.Kind == JobKind.Convert
        && Path.GetExtension(request.InputPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
        && Path.GetExtension(request.OutputPath).Equals(".pptx", StringComparison.OrdinalIgnoreCase);

    public Task<JobResult> ExecuteAsync(DocumentJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Convert(request, progress, cancellationToken), cancellationToken);

    private static JobResult Convert(DocumentJobRequest request, IProgress<JobProgress>? progress, CancellationToken token)
    {
        using var workspace = new JobWorkspace(request.JobId);
        var pages = PdfRenderingService.Render(request.InputPath, workspace.PathFor("pages"), request.Conversion.RenderDpi, token);
        var temporary = workspace.PathFor("output.pptx");

        using (var presentation = PresentationDocument.Create(temporary, PresentationDocumentType.Presentation))
        {
            var presentationPart = CreatePresentationScaffold(presentation);
            var presentationRoot = presentationPart.Presentation
                ?? throw new InvalidOperationException("演示文稿缺少根节点。");
            uint slideId = 256;
            for (var index = 0; index < pages.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var rendered = pages[index];
                var slidePart = presentationPart.AddNewPart<SlidePart>();
                slidePart.AddPart(presentationPart.SlideMasterParts.First().SlideLayoutParts.First());
                slidePart.Slide = BuildSlide(slidePart, rendered);
                var relationshipId = presentationPart.GetIdOfPart(slidePart);
                var slideIdList = presentationRoot.SlideIdList
                    ?? throw new InvalidOperationException("演示文稿缺少幻灯片列表。");
                slideIdList.Append(new P.SlideId { Id = slideId++, RelationshipId = relationshipId });
                progress?.Report(new JobProgress((index + 1d) / pages.Count * 95, $"正在生成幻灯片 {index + 1}/{pages.Count}"));
            }

            presentationRoot.Save();
        }

        ValidatePresentation(temporary);
        workspace.Commit(temporary, request.OutputPath);
        return JobResult.Ok(request.OutputPath);
    }

    private static PresentationPart CreatePresentationScaffold(PresentationDocument document)
    {
        var presentationPart = document.AddPresentationPart();
        presentationPart.Presentation = new P.Presentation(new P.SlideMasterIdList(), new P.SlideIdList());

        var masterPart = presentationPart.AddNewPart<SlideMasterPart>();
        var layoutPart = masterPart.AddNewPart<SlideLayoutPart>();
        layoutPart.SlideLayout = new P.SlideLayout(
            new P.CommonSlideData(CreateShapeTree()),
            new P.ColorMapOverride(new A.MasterColorMapping())) { Type = P.SlideLayoutValues.Blank, Preserve = true };
        layoutPart.SlideLayout.Save();

        var themePart = masterPart.AddNewPart<ThemePart>();
        themePart.Theme = CreateTheme();
        themePart.Theme.Save();

        masterPart.SlideMaster = new P.SlideMaster(
            new P.CommonSlideData(CreateShapeTree()),
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1,
                Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5,
                Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
            },
            new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649U, RelationshipId = masterPart.GetIdOfPart(layoutPart) }),
            new P.TextStyles(new P.TitleStyle(), new P.BodyStyle(), new P.OtherStyle()));
        masterPart.SlideMaster.Save();

        var masterIdList = presentationPart.Presentation.SlideMasterIdList
            ?? throw new InvalidOperationException("演示文稿缺少母版列表。");
        masterIdList.Append(
            new P.SlideMasterId { Id = 2147483648U, RelationshipId = presentationPart.GetIdOfPart(masterPart) });
        presentationPart.Presentation.SlideSize = new P.SlideSize { Cx = (int)SlideWidth, Cy = (int)SlideHeight, Type = P.SlideSizeValues.Screen16x9 };
        presentationPart.Presentation.NotesSize = new P.NotesSize { Cx = 6858000, Cy = 9144000 };
        return presentationPart;
    }

    private static P.Slide BuildSlide(SlidePart slidePart, RenderedPdfPage rendered)
    {
        var scale = Math.Min(SlideWidth / rendered.WidthPoints, SlideHeight / rendered.HeightPoints);
        var pageWidth = (long)Math.Round(rendered.WidthPoints * scale);
        var pageHeight = (long)Math.Round(rendered.HeightPoints * scale);
        var offsetX = (SlideWidth - pageWidth) / 2;
        var offsetY = (SlideHeight - pageHeight) / 2;
        var tree = CreateShapeTree();
        var imagePart = slidePart.AddImagePart(ImagePartType.Png);
        using (var stream = File.OpenRead(rendered.ImagePath)) imagePart.FeedData(stream);
        tree.Append(CreatePicture(slidePart.GetIdOfPart(imagePart), offsetX, offsetY, pageWidth, pageHeight));

        var commonSlideData = new P.CommonSlideData(
            new P.Background(
                new P.BackgroundProperties(
                    new A.SolidFill(new A.RgbColorModelHex { Val = "F2F0ED" }))),
            tree);

        return new P.Slide(
            commonSlideData,
            new P.ColorMapOverride(new A.MasterColorMapping()));
    }

    private static P.ShapeTree CreateShapeTree() => new(
        new P.NonVisualGroupShapeProperties(
            new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
            new P.NonVisualGroupShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties()),
        new P.GroupShapeProperties(new A.TransformGroup(
            new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = 0L, Cy = 0L },
            new A.ChildOffset { X = 0L, Y = 0L }, new A.ChildExtents { Cx = 0L, Cy = 0L })));

    private static P.Picture CreatePicture(string relationshipId, long x, long y, long width, long height) => new(
        new P.NonVisualPictureProperties(
            new P.NonVisualDrawingProperties { Id = 2U, Name = "PDF 页面背景" },
            new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
            new P.ApplicationNonVisualDrawingProperties()),
        new P.BlipFill(new A.Blip { Embed = relationshipId }, new A.Stretch(new A.FillRectangle())),
        new P.ShapeProperties(
            new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = width, Cy = height }),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));

    private static void ValidatePresentation(string path)
    {
        using var presentation = PresentationDocument.Open(path, false);
        var errors = new OpenXmlValidator().Validate(presentation).Take(10).ToArray();
        if (errors.Length == 0) return;
        throw new InvalidDataException("生成的 PPTX 未通过 Open XML 验证：" +
            string.Join("；", errors.Select(error => error.Description)));
    }

    private static A.Theme CreateTheme()
    {
        var fillStyles = new A.FillStyleList(
            ThemeSolidFill(),
            ThemeSolidFill(),
            ThemeSolidFill());
        var lineStyles = new A.LineStyleList(
            ThemeOutline(6350),
            ThemeOutline(12700),
            ThemeOutline(19050));
        var effectStyles = new A.EffectStyleList(
            new A.EffectStyle(new A.EffectList()),
            new A.EffectStyle(new A.EffectList()),
            new A.EffectStyle(new A.EffectList()));
        var backgroundStyles = new A.BackgroundFillStyleList(
            ThemeSolidFill(),
            ThemeSolidFill(),
            ThemeSolidFill());

        var colorScheme = new A.ColorScheme(
            new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText, LastColor = "000000" }),
            new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window, LastColor = "FFFFFF" }),
            new A.Dark2Color(new A.RgbColorModelHex { Val = "1F2937" }),
            new A.Light2Color(new A.RgbColorModelHex { Val = "F3F4F6" }),
            new A.Accent1Color(new A.RgbColorModelHex { Val = "2563EB" }),
            new A.Accent2Color(new A.RgbColorModelHex { Val = "0F766E" }),
            new A.Accent3Color(new A.RgbColorModelHex { Val = "CA8A04" }),
            new A.Accent4Color(new A.RgbColorModelHex { Val = "DC2626" }),
            new A.Accent5Color(new A.RgbColorModelHex { Val = "7C3AED" }),
            new A.Accent6Color(new A.RgbColorModelHex { Val = "0891B2" }),
            new A.Hyperlink(new A.RgbColorModelHex { Val = "0000FF" }),
            new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "800080" })) { Name = "DocConvert" };
        var fontScheme = new A.FontScheme(
            new A.MajorFont(new A.LatinFont { Typeface = "Arial" }, new A.EastAsianFont { Typeface = "Microsoft YaHei" }, new A.ComplexScriptFont { Typeface = "Arial" }),
            new A.MinorFont(new A.LatinFont { Typeface = "Arial" }, new A.EastAsianFont { Typeface = "Microsoft YaHei" }, new A.ComplexScriptFont { Typeface = "Arial" })) { Name = "DocConvert" };

        var formatScheme = new A.FormatScheme { Name = "DocConvert" };
        formatScheme.FillStyleList = fillStyles;
        formatScheme.LineStyleList = lineStyles;
        formatScheme.EffectStyleList = effectStyles;
        formatScheme.BackgroundFillStyleList = backgroundStyles;
        return new A.Theme
        {
            Name = "DocConvert",
            ThemeElements = new A.ThemeElements
            {
                ColorScheme = colorScheme,
                FontScheme = fontScheme,
                FormatScheme = formatScheme
            }
        };
    }

    private static A.SolidFill ThemeSolidFill() =>
        new(new A.SchemeColor { Val = A.SchemeColorValues.PhColor });

    private static A.Outline ThemeOutline(int width) => new(
        ThemeSolidFill(),
        new A.PresetDash { Val = A.PresetLineDashValues.Solid },
        new A.Miter { Limit = 800000 })
    {
        Width = width,
        CapType = A.LineCapValues.Flat,
        CompoundLineType = A.CompoundLineValues.Single,
        Alignment = A.PenAlignmentValues.Center
    };
}

internal static class OpenXmlExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
