using System;
using System.Globalization;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace TeletextRecoveReese;

public partial class AboutWindow : Window
{
    private readonly DispatcherTimer _titleTimer;

    public AboutWindow()
    {
        InitializeComponent();
        Title = $"About {AppVersion.ProductName}";
        AnimatedTitle.FontFamilyName = App.GridFontFamilyName;
        AnimatedTitle.TerminalFontFamily = App.GridFontFamily;
        AnimatedTitle.TerminatorMode = App.TerminatorAboutUnlocked;
        BlackImageOverlay.IsVisible = App.TerminatorAboutUnlocked;
        _titleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _titleTimer.Tick += (_, _) =>
        {
            if (!AnimatedTitle.Advance())
                _titleTimer.Stop();
        };
        _titleTimer.Start();
        Closed += (_, _) => _titleTimer.Stop();
    }

    /// <summary>
    /// Handles the close button click event to dismiss the About window.
    /// </summary>
    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnSupportLinkClicked(object? sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://ko-fi.com/sinisinavideoteka"));
    }

    private async void OnRepositoryLinkClicked(object? sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://github.com/sdosenn/TeletextRecoveReese"));
    }
}

public sealed class TerminatorTitleControl : Control
{
    private const string TitleText = "TELETEXTRECOVEREESE";
    private const string FinalTitleText = "TeletextRecoveReese";
    private const double FontSize = 440;
    private const double TerminalFontSize = 18;
    private const double ApproachSeconds = 18.0;
    private const double AssembledHoldSeconds = 0.25;
    private const double ZoomSeconds = 3.2;
    private const double FlyToTopSeconds = 1.15;
    private const double FinalCasingSeconds = 0.7;
    private const double FinalShadowFadeSeconds = 0.4;
    private const double VersionLabelSeconds = 0.24;
    private const double VersionLiftSeconds = 0.12;
    private const double VersionValueSeconds = 0.24;
    private const double VersionHoldSeconds = 1.0;
    private const double VersionValueHoldSeconds = 1.4;
    private const double VersionCursorBlinkSeconds = 0.6;
    private const double VersionGreenTransitionSeconds = 0.65;
    private const double ContributorsLabelSeconds = 0.36;
    private const double ContributorsValueSeconds = 0.28;
    private const double ContributorsGreenTransitionSeconds = 0.65;
    private const double EasterEggDelaySeconds = 10.0;
    private const double EasterEggTypeSeconds = 0.4;
    private const double SecondsPerTick = 0.016;
    private static readonly int[] LowercaseRevealOrder =
        [10, 3, 16, 1, 12, 6, 18, 9, 4, 15, 2, 11, 17, 5, 13, 7];
    private double _elapsedSeconds;

    public string? FontFamilyName { get; set; }
    public FontFamily? TerminalFontFamily { get; set; }
    public bool TerminatorMode { get; set; }

    public bool Advance()
    {
        _elapsedSeconds += SecondsPerTick;
        double flyEnd = ApproachSeconds + AssembledHoldSeconds + ZoomSeconds + FlyToTopSeconds;
        double titleEnd = flyEnd + FinalCasingSeconds + FinalShadowFadeSeconds;
        double versionEnd = VersionLabelSeconds + VersionLiftSeconds
                            + VersionValueSeconds + VersionValueHoldSeconds
                            + VersionCursorBlinkSeconds + VersionGreenTransitionSeconds;
        double contributorsStart = VersionLabelSeconds + VersionLiftSeconds
                                   + VersionValueSeconds + VersionValueHoldSeconds
                                   + VersionCursorBlinkSeconds;
        double contributorsEnd = contributorsStart + VersionLiftSeconds
                                 + ContributorsLabelSeconds + VersionLiftSeconds
                                 + ContributorsValueSeconds + VersionValueHoldSeconds
                                 + VersionCursorBlinkSeconds
                                 + ContributorsGreenTransitionSeconds;
        double firstSequenceEnd = Math.Max(titleEnd, Math.Max(versionEnd, contributorsEnd));
        double sequenceSeconds = TerminatorMode
            ? Math.Max(
                titleEnd,
                flyEnd + VersionLabelSeconds + VersionLiftSeconds + VersionValueSeconds
                + VersionHoldSeconds + VersionCursorBlinkSeconds + VersionGreenTransitionSeconds)
            : firstSequenceEnd + EasterEggDelaySeconds + VersionLiftSeconds
              + EasterEggTypeSeconds + VersionHoldSeconds
              + VersionCursorBlinkSeconds + VersionGreenTransitionSeconds;
        if (_elapsedSeconds > sequenceSeconds)
            _elapsedSeconds = sequenceSeconds;
        InvalidateVisual();
        return _elapsedSeconds < sequenceSeconds;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        string familyName = string.IsNullOrWhiteSpace(FontFamilyName)
            ? "Menlo,DejaVu Sans Mono,monospace"
            : FontFamilyName;
        var typeface = new Typeface(
            new FontFamily(familyName),
            FontStyle.Normal,
            FontWeight.Bold);
        double flyEnd = ApproachSeconds + AssembledHoldSeconds + ZoomSeconds + FlyToTopSeconds;
        double casingProgress = Math.Clamp((_elapsedSeconds - flyEnd) / FinalCasingSeconds, 0, 1);
        double finalShadowProgress = Math.Clamp(
            (_elapsedSeconds - flyEnd - FinalCasingSeconds) / FinalShadowFadeSeconds,
            0,
            1);
        string activeTitle = TerminatorMode ? "TERMINATOR" : TitleText;
        string displayedTitle = TerminatorMode ? activeTitle : BuildProgressiveCasing(casingProgress);
        int splitIndex = displayedTitle.Length / 2;
        string beginning = displayedTitle[..splitIndex];
        string ending = displayedTitle[splitIndex..];
        int uppercaseSplitIndex = activeTitle.Length / 2;
        FormattedText baseBeginningText = CreateText(activeTitle[..uppercaseSplitIndex], typeface, FontSize);
        FormattedText baseEndingText = CreateText(activeTitle[uppercaseSplitIndex..], typeface, FontSize);
        double baseWidth = baseBeginningText.WidthIncludingTrailingWhitespace
                           + baseEndingText.WidthIncludingTrailingWhitespace;
        double fittedScale = Math.Min(1.0, Math.Max(Bounds.Width - 32, 1) / baseWidth);
        double zoomStart = ApproachSeconds + AssembledHoldSeconds;
        double zoomProgress = Math.Clamp((_elapsedSeconds - zoomStart) / ZoomSeconds, 0, 1);
        double scale = Lerp(1.0, fittedScale, EaseInOut(zoomProgress));

        FormattedText beginningText = CreateText(beginning, typeface, FontSize * scale);
        FormattedText endingText = CreateText(ending, typeface, FontSize * scale);
        double totalWidth = beginningText.WidthIncludingTrailingWhitespace
                            + endingText.WidthIncludingTrailingWhitespace;
        double finalBeginningX = (Bounds.Width - totalWidth) / 2;
        double finalEndingX = finalBeginningX + beginningText.WidthIncludingTrailingWhitespace;
        double centeredY = Bounds.Height / 2
                           - Math.Max(beginningText.Height, endingText.Height) / 2
                           + 18 * scale;
        double flyProgress = Math.Clamp(
            (_elapsedSeconds - zoomStart - ZoomSeconds) / FlyToTopSeconds,
            0,
            1);
        double y = Lerp(centeredY, 12, EaseInOut(flyProgress));

        double beginningX = finalBeginningX;
        double endingX = finalEndingX;
        if (_elapsedSeconds < ApproachSeconds)
        {
            double progress = Math.Clamp(_elapsedSeconds / ApproachSeconds, 0, 1);
            beginningX = Lerp(Bounds.Width + 30, finalBeginningX, progress);
            endingX = Lerp(-endingText.WidthIncludingTrailingWhitespace - 30, finalEndingX, progress);
        }

        Geometry? beginningOutline = beginningText.BuildGeometry(new Point(beginningX, y));
        Geometry? endingOutline = endingText.BuildGeometry(new Point(endingX, y));
        if (beginningOutline is null || endingOutline is null) return;

        double fillProgress = Math.Clamp((_elapsedSeconds - ApproachSeconds) / 1.0, 0, 1);
        double shadowStrength = Lerp(0.5, 1, EaseInOut(finalShadowProgress));
        DrawFinalShadow(context, beginningOutline, scale, shadowStrength);
        DrawFinalShadow(context, endingOutline, scale, shadowStrength);
        DrawTitle(context, beginningOutline, scale, fillProgress);
        DrawTitle(context, endingOutline, scale, fillProgress);

        if (zoomProgress > 0 && zoomProgress < 1)
        {
            double shineCenter = Lerp(finalBeginningX - 35, finalEndingX + endingText.Width + 35, zoomProgress);
            using (context.PushClip(new Rect(shineCenter - 28, 0, 56, Bounds.Height)))
            {
                var shine = new SolidColorBrush(Color.Parse("#6CA6C4"));
                context.DrawGeometry(shine, null, beginningOutline);
                context.DrawGeometry(shine, null, endingOutline);
            }
        }

        if (TerminatorMode)
        {
            DrawTerminatorTerminalSequence(context, typeface, flyEnd);
        }
        else
        {
            double titleEnd = flyEnd + FinalCasingSeconds + FinalShadowFadeSeconds;
            double versionEnd = VersionLabelSeconds + VersionLiftSeconds
                                + VersionValueSeconds + VersionValueHoldSeconds
                                + VersionCursorBlinkSeconds + VersionGreenTransitionSeconds;
            double contributorsStart = VersionLabelSeconds + VersionLiftSeconds
                                       + VersionValueSeconds + VersionValueHoldSeconds
                                       + VersionCursorBlinkSeconds;
            double contributorsEnd = contributorsStart + VersionLiftSeconds
                                     + ContributorsLabelSeconds + VersionLiftSeconds
                                     + ContributorsValueSeconds + VersionValueHoldSeconds
                                     + VersionCursorBlinkSeconds
                                     + ContributorsGreenTransitionSeconds;
            double easterEggStart = Math.Max(titleEnd, Math.Max(versionEnd, contributorsEnd))
                                    + EasterEggDelaySeconds;
            DrawVersionSequence(context, typeface, 0, contributorsStart, easterEggStart);
        }
    }

    private void DrawTerminatorTerminalSequence(
        DrawingContext context,
        Typeface typeface,
        double startTime)
    {
        double elapsed = _elapsedSeconds - startTime;
        if (elapsed < 0)
            return;

        const string model = "T-800";
        const string chassis = "CSM-101";
        int modelCharacters = Math.Min(
            model.Length,
            (int)Math.Ceiling(Math.Clamp(elapsed / VersionLabelSeconds, 0, 1) * model.Length));
        double liftProgress = Math.Clamp(
            (elapsed - VersionLabelSeconds) / VersionLiftSeconds,
            0,
            1);
        double chassisElapsed = elapsed - VersionLabelSeconds - VersionLiftSeconds;
        int chassisCharacters = Math.Min(
            chassis.Length,
            (int)Math.Ceiling(Math.Clamp(chassisElapsed / VersionValueSeconds, 0, 1) * chassis.Length));
        double blinkElapsed = chassisElapsed - VersionValueSeconds - VersionHoldSeconds;
        bool blinkFinished = blinkElapsed >= VersionCursorBlinkSeconds;
        bool cursorVisible = !blinkFinished
                             && (blinkElapsed < 0
                                 || (int)(blinkElapsed / (VersionCursorBlinkSeconds / 12)) % 2 == 0);
        double redProgress = EaseInOut(Math.Clamp(
            (blinkElapsed - VersionCursorBlinkSeconds) / VersionGreenTransitionSeconds,
            0,
            1));
        IBrush textBrush = CreateTerminalBrush(redProgress, 0xFF, 0x28, 0x28);
        var terminalTypeface = new Typeface(
            TerminalFontFamily ?? typeface.FontFamily,
            FontStyle.Normal,
            FontWeight.Bold);
        FormattedText modelText = CreateText(model[..modelCharacters], terminalTypeface, TerminalFontSize);
        FormattedText chassisText = CreateText(chassis[..chassisCharacters], terminalTypeface, TerminalFontSize);
        double cursorX = 18;
        double textX = cursorX + 17;
        double bottomY = Math.Max(0, Bounds.Height - Math.Max(modelText.Height, chassisText.Height) - 16);
        double lineStep = Math.Max(modelText.Height, 20) + 3;
        double modelY = Lerp(bottomY, bottomY - lineStep, EaseInOut(liftProgress));

        DrawTerminalText(context, modelText, new Point(textX, modelY), textBrush);
        if (chassisElapsed >= 0)
            DrawTerminalText(context, chassisText, new Point(textX, bottomY), textBrush);

        if (cursorVisible)
            DrawTerminalCursor(context, new Rect(cursorX, bottomY + 2, 10, Math.Max(14, modelText.Height - 3)), textBrush);
    }

    private void DrawVersionSequence(
        DrawingContext context,
        Typeface typeface,
        double startTime,
        double contributorsStart,
        double easterEggStart)
    {
        double elapsed = _elapsedSeconds - startTime;
        if (elapsed < 0)
            return;

        const string label = "VERSION:";
        const string value = "0.9 BETA";
        int labelCharacters = Math.Min(
            label.Length,
            (int)Math.Ceiling(Math.Clamp(elapsed / VersionLabelSeconds, 0, 1) * label.Length));
        double liftProgress = Math.Clamp(
            (elapsed - VersionLabelSeconds) / VersionLiftSeconds,
            0,
            1);
        double valueElapsed = elapsed - VersionLabelSeconds - VersionLiftSeconds;
        int valueCharacters = Math.Min(
            value.Length,
            (int)Math.Ceiling(Math.Clamp(valueElapsed / VersionValueSeconds, 0, 1) * value.Length));
        string visibleLabel = label[..labelCharacters];
        string visibleValue = value[..valueCharacters];

        double blinkElapsed = valueElapsed - VersionValueSeconds - VersionValueHoldSeconds;
        bool blinkFinished = blinkElapsed >= VersionCursorBlinkSeconds;
        bool cursorVisible = !blinkFinished
                             && (blinkElapsed < 0
                                 || (int)(blinkElapsed / (VersionCursorBlinkSeconds / 12)) % 2 == 0);
        double greenProgress = Math.Clamp(
            (blinkElapsed - VersionCursorBlinkSeconds) / VersionGreenTransitionSeconds,
            0,
            1);
        greenProgress = EaseInOut(greenProgress);
        IBrush textBrush = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round(Lerp(210, 220, greenProgress)),
            (byte)Math.Round(Lerp(0xFF, 0x39, greenProgress)),
            0xFF,
            (byte)Math.Round(Lerp(0xFF, 0x5A, greenProgress))));
        var terminalTypeface = new Typeface(
            TerminalFontFamily ?? typeface.FontFamily,
            FontStyle.Normal,
            FontWeight.Bold);
        FormattedText labelText = CreateText(visibleLabel, terminalTypeface, TerminalFontSize);
        FormattedText valueText = CreateText(visibleValue, terminalTypeface, TerminalFontSize);

        const string contributorsLabel = "CONTRIBUTORS:";
        const string contributorName = "NIGEL REED";
        double contributorsElapsed = elapsed - contributorsStart;
        bool contributorsActive = contributorsElapsed >= 0;
        double contributorsLayoutProgress = Math.Clamp(
            contributorsElapsed / VersionLiftSeconds,
            0,
            1);
        double contributorsLabelElapsed = contributorsElapsed - VersionLiftSeconds;
        int contributorsLabelCharacters = Math.Min(
            contributorsLabel.Length,
            (int)Math.Ceiling(
                Math.Clamp(contributorsLabelElapsed / ContributorsLabelSeconds, 0, 1)
                * contributorsLabel.Length));
        double contributorNameElapsed = contributorsLabelElapsed
                                        - ContributorsLabelSeconds
                                        - VersionLiftSeconds;
        int contributorNameCharacters = Math.Min(
            contributorName.Length,
            (int)Math.Ceiling(
                Math.Clamp(contributorNameElapsed / ContributorsValueSeconds, 0, 1)
                * contributorName.Length));
        FormattedText contributorsLabelText = CreateText(
            contributorsLabel[..contributorsLabelCharacters],
            terminalTypeface,
            TerminalFontSize);
        FormattedText contributorNameText = CreateText(
            contributorName[..contributorNameCharacters],
            terminalTypeface,
            TerminalFontSize);
        double contributorsBlinkElapsed = contributorNameElapsed
                                           - ContributorsValueSeconds
                                           - VersionValueHoldSeconds;
        bool contributorsBlinkFinished =
            contributorsBlinkElapsed >= VersionCursorBlinkSeconds;
        bool contributorsCursorVisible = contributorsActive
                                          && !contributorsBlinkFinished
                                          && (contributorsBlinkElapsed < 0
                                              || (int)(contributorsBlinkElapsed
                                                       / (VersionCursorBlinkSeconds / 12)) % 2 == 0);
        double contributorsGreenProgress = EaseInOut(Math.Clamp(
            (contributorsBlinkElapsed - VersionCursorBlinkSeconds)
            / ContributorsGreenTransitionSeconds,
            0,
            1));
        IBrush contributorsBrush = CreateTerminalBrush(contributorsGreenProgress);

        const string easterEggText = "TYPE REESE";
        double easterEggElapsed = _elapsedSeconds - easterEggStart;
        bool easterEggActive = easterEggElapsed >= 0;
        double easterEggLiftProgress = Math.Clamp(
            easterEggElapsed / VersionLiftSeconds,
            0,
            1);
        double easterEggTypeElapsed = easterEggElapsed - VersionLiftSeconds;
        int easterEggCharacters = Math.Min(
            easterEggText.Length,
            (int)Math.Ceiling(
                Math.Clamp(easterEggTypeElapsed / EasterEggTypeSeconds, 0, 1)
                * easterEggText.Length));
        FormattedText easterEggLine = CreateText(
            easterEggText[..easterEggCharacters],
            terminalTypeface,
            TerminalFontSize);
        double easterEggBlinkElapsed = easterEggTypeElapsed - EasterEggTypeSeconds - VersionHoldSeconds;
        bool easterEggBlinkFinished = easterEggBlinkElapsed >= VersionCursorBlinkSeconds;
        bool easterEggCursorVisible = easterEggActive
                                      && !easterEggBlinkFinished
                                      && (easterEggBlinkElapsed < 0
                                          || (int)(easterEggBlinkElapsed
                                                   / (VersionCursorBlinkSeconds / 12)) % 2 == 0);
        double easterEggGreenProgress = EaseInOut(Math.Clamp(
            (easterEggBlinkElapsed - VersionCursorBlinkSeconds) / VersionGreenTransitionSeconds,
            0,
            1));
        IBrush easterEggBrush = CreateTerminalBrush(easterEggGreenProgress);
        double cursorX = 18;
        double textX = cursorX + 17;
        double bottomY = Math.Max(0, Bounds.Height - Math.Max(labelText.Height, valueText.Height) - 16);
        double lineStep = Math.Max(labelText.Height, 20) + 3;
        double upperY = bottomY - lineStep;
        double contributorsLift = EaseInOut(contributorsLayoutProgress) * lineStep * 3;
        double easterEggLift = EaseInOut(easterEggLiftProgress) * lineStep * 2;
        double extraLift = contributorsLift + easterEggLift;
        double labelY = Lerp(bottomY, upperY, EaseInOut(liftProgress)) - extraLift;
        double valueY = bottomY - extraLift;
        Geometry? labelGeometry = labelText.BuildGeometry(new Point(textX, labelY));
        if (labelGeometry is not null)
        {
            DrawTerminalShadow(context, labelGeometry);
            context.DrawGeometry(textBrush, null, labelGeometry);
        }
        if (valueElapsed >= 0)
        {
            Geometry? valueGeometry = valueText.BuildGeometry(new Point(textX, valueY));
            if (valueGeometry is not null)
            {
                DrawTerminalShadow(context, valueGeometry);
                context.DrawGeometry(textBrush, null, valueGeometry);
            }
        }

        double contributorsLabelY = upperY - easterEggLift;
        double contributorNameY = bottomY - easterEggLift;
        if (contributorsLabelElapsed >= 0)
            DrawTerminalText(
                context,
                contributorsLabelText,
                new Point(textX, contributorsLabelY),
                contributorsBrush);
        if (contributorNameElapsed >= 0)
            DrawTerminalText(
                context,
                contributorNameText,
                new Point(textX, contributorNameY),
                contributorsBrush);

        if (easterEggTypeElapsed >= 0)
        {
            Geometry? easterEggGeometry = easterEggLine.BuildGeometry(new Point(textX, bottomY));
            if (easterEggGeometry is not null)
            {
                DrawTerminalShadow(context, easterEggGeometry);
                context.DrawGeometry(easterEggBrush, null, easterEggGeometry);
            }
        }

        bool showCursor = easterEggActive
            ? easterEggCursorVisible
            : contributorsActive
                ? contributorsCursorVisible
                : cursorVisible;
        if (showCursor)
        {
            double cursorY = contributorsActive
                             && !easterEggActive
                             && contributorNameElapsed < 0
                ? upperY
                : bottomY;
            var cursorRect = new Rect(cursorX, cursorY + 2, 10, Math.Max(14, labelText.Height - 3));
            context.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                null,
                cursorRect.Translate(new Vector(2, 2)));
            context.DrawRectangle(
                easterEggActive
                    ? easterEggBrush
                    : contributorsActive
                        ? contributorsBrush
                        : textBrush,
                null,
                cursorRect);
        }
    }

    private static void DrawTerminalText(
        DrawingContext context,
        FormattedText text,
        Point position,
        IBrush brush)
    {
        Geometry? geometry = text.BuildGeometry(position);
        if (geometry is null)
            return;
        DrawTerminalShadow(context, geometry);
        context.DrawGeometry(brush, null, geometry);
    }

    private static void DrawTerminalCursor(DrawingContext context, Rect rect, IBrush brush)
    {
        context.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            null,
            rect.Translate(new Vector(2, 2)));
        context.DrawRectangle(brush, null, rect);
    }

    private static IBrush CreateTerminalBrush(double greenProgress) =>
        CreateTerminalBrush(greenProgress, 0x39, 0xFF, 0x5A);

    private static IBrush CreateTerminalBrush(
        double progress,
        byte finalRed,
        byte finalGreen,
        byte finalBlue) =>
        new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round(Lerp(210, 220, progress)),
            (byte)Math.Round(Lerp(0xFF, finalRed, progress)),
            (byte)Math.Round(Lerp(0xFF, finalGreen, progress)),
            (byte)Math.Round(Lerp(0xFF, finalBlue, progress))));

    private static void DrawTerminalShadow(DrawingContext context, Geometry geometry)
    {
        using (context.PushTransform(Matrix.CreateTranslation(2, 2)))
        {
            context.DrawGeometry(
                new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                null,
                geometry);
        }
    }

    private static FormattedText CreateText(string text, Typeface typeface, double fontSize)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Transparent)
        {
            MaxTextWidth = 10000,
            MaxLineCount = 1,
        };
        return formatted;
    }

    private static string BuildProgressiveCasing(double progress)
    {
        if (progress <= 0)
            return TitleText;
        if (progress >= 1)
            return FinalTitleText;

        char[] characters = TitleText.ToCharArray();
        int revealedCount = Math.Min(
            LowercaseRevealOrder.Length,
            (int)Math.Floor(progress * (LowercaseRevealOrder.Length + 1)));
        for (int i = 0; i < revealedCount; i++)
        {
            int characterIndex = LowercaseRevealOrder[i];
            characters[characterIndex] = FinalTitleText[characterIndex];
        }
        return new string(characters);
    }

    private static double EaseInOut(double value) =>
        value < 0.5
            ? 4 * value * value * value
            : 1 - Math.Pow(-2 * value + 2, 3) / 2;

    private static double Lerp(double start, double end, double amount) =>
        start + (end - start) * amount;

    private static void DrawTitle(
        DrawingContext context,
        Geometry outline,
        double scale,
        double fillProgress)
    {
        if (fillProgress > 0)
        {
            byte alpha = (byte)Math.Round(255 * fillProgress);
            context.DrawGeometry(
                new SolidColorBrush(Color.FromArgb(alpha, 0xF4, 0xF6, 0xF8)),
                null,
                outline);
        }
        Color outer = BlendColor(0x05, 0x0C, 0x17, 0x00, 0x00, 0x00, fillProgress);
        Color middle = BlendColor(0x17, 0x47, 0x64, 0x02, 0x02, 0x02, fillProgress);
        Color inner = BlendColor(0x4F, 0x7F, 0x9B, 0x08, 0x08, 0x08, fillProgress);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(outer), Math.Max(1, 8 * scale)), outline);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(middle), Math.Max(0.65, 4 * scale)), outline);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(inner), Math.Max(0.35, 1.25 * scale)), outline);
    }

    private static void DrawFinalShadow(
        DrawingContext context,
        Geometry outline,
        double scale,
        double progress)
    {
        byte alpha = (byte)Math.Round(190 * progress);
        double offset = Math.Max(2, 7 * scale);
        using (context.PushTransform(Matrix.CreateTranslation(offset, offset)))
        {
            context.DrawGeometry(
                new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)),
                new Pen(
                    new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)),
                    Math.Max(1.5, 6 * scale)),
                outline);
        }
    }

    private static Color BlendColor(
        byte startR,
        byte startG,
        byte startB,
        byte endR,
        byte endG,
        byte endB,
        double amount) =>
        Color.FromRgb(
            (byte)Math.Round(Lerp(startR, endR, amount)),
            (byte)Math.Round(Lerp(startG, endG, amount)),
            (byte)Math.Round(Lerp(startB, endB, amount)));
}
