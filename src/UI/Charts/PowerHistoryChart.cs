using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace task_monitor
{
    /// <summary>
    /// Lightweight WPF power history chart. Unlike UsageHistoryChart, this chart
    /// auto-scales watts and keeps gaps where a sensor did not provide a reading.
    /// </summary>
    public sealed class PowerHistoryChart : FrameworkElement
    {
        public static readonly DependencyProperty SystemValuesProperty =
            DependencyProperty.Register(
                nameof(SystemValues),
                typeof(IReadOnlyList<double?>),
                typeof(PowerHistoryChart),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty CpuPackageValuesProperty =
            DependencyProperty.Register(
                nameof(CpuPackageValues),
                typeof(IReadOnlyList<double?>),
                typeof(PowerHistoryChart),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BatteryChargeValuesProperty =
            DependencyProperty.Register(
                nameof(BatteryChargeValues),
                typeof(IReadOnlyList<double?>),
                typeof(PowerHistoryChart),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BatteryDischargeValuesProperty =
            DependencyProperty.Register(
                nameof(BatteryDischargeValues),
                typeof(IReadOnlyList<double?>),
                typeof(PowerHistoryChart),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty HistoryDurationSecondsProperty =
            DependencyProperty.Register(
                nameof(HistoryDurationSeconds),
                typeof(int),
                typeof(PowerHistoryChart),
                new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BackgroundProperty =
            DependencyProperty.Register(
                nameof(Background),
                typeof(Brush),
                typeof(PowerHistoryChart),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SystemColorProperty =
            DependencyProperty.Register(
                nameof(SystemColor),
                typeof(Color),
                typeof(PowerHistoryChart),
                new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0x78, 0xD4), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty CpuPackageColorProperty =
            DependencyProperty.Register(
                nameof(CpuPackageColor),
                typeof(Color),
                typeof(PowerHistoryChart),
                new FrameworkPropertyMetadata(Color.FromRgb(0xF7, 0x63, 0x0C), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BatteryColorProperty =
            DependencyProperty.Register(
                nameof(BatteryColor),
                typeof(Color),
                typeof(PowerHistoryChart),
                new FrameworkPropertyMetadata(Color.FromRgb(0x10, 0xB9, 0x81), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BatteryChargeColorProperty =
            DependencyProperty.Register(
                nameof(BatteryChargeColor),
                typeof(Color),
                typeof(PowerHistoryChart),
                new FrameworkPropertyMetadata(Color.FromRgb(0x8B, 0x5C, 0xF6), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GridColorProperty =
            DependencyProperty.Register(
                nameof(GridColor),
                typeof(Color),
                typeof(PowerHistoryChart),
                new FrameworkPropertyMetadata(Color.FromArgb(0x33, 0x00, 0x00, 0x00), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FrameColorProperty =
            DependencyProperty.Register(
                nameof(FrameColor),
                typeof(Color),
                typeof(PowerHistoryChart),
                new FrameworkPropertyMetadata(Color.FromArgb(0x66, 0x00, 0x00, 0x00), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FrameCornerRadiusProperty =
            DependencyProperty.Register(
                nameof(FrameCornerRadius),
                typeof(double),
                typeof(PowerHistoryChart),
                new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public IReadOnlyList<double?> SystemValues
        {
            get => (IReadOnlyList<double?>)GetValue(SystemValuesProperty);
            set => SetValue(SystemValuesProperty, value);
        }

        public IReadOnlyList<double?> CpuPackageValues
        {
            get => (IReadOnlyList<double?>)GetValue(CpuPackageValuesProperty);
            set => SetValue(CpuPackageValuesProperty, value);
        }

        public IReadOnlyList<double?> BatteryChargeValues
        {
            get => (IReadOnlyList<double?>)GetValue(BatteryChargeValuesProperty);
            set => SetValue(BatteryChargeValuesProperty, value);
        }

        public IReadOnlyList<double?> BatteryDischargeValues
        {
            get => (IReadOnlyList<double?>)GetValue(BatteryDischargeValuesProperty);
            set => SetValue(BatteryDischargeValuesProperty, value);
        }

        public int HistoryDurationSeconds
        {
            get => (int)GetValue(HistoryDurationSecondsProperty);
            set => SetValue(HistoryDurationSecondsProperty, value);
        }

        public Brush Background
        {
            get => (Brush)GetValue(BackgroundProperty);
            set => SetValue(BackgroundProperty, value);
        }

        public Color SystemColor
        {
            get => (Color)GetValue(SystemColorProperty);
            set => SetValue(SystemColorProperty, value);
        }

        public Color CpuPackageColor
        {
            get => (Color)GetValue(CpuPackageColorProperty);
            set => SetValue(CpuPackageColorProperty, value);
        }

        public Color BatteryColor
        {
            get => (Color)GetValue(BatteryColorProperty);
            set => SetValue(BatteryColorProperty, value);
        }

        public Color BatteryChargeColor
        {
            get => (Color)GetValue(BatteryChargeColorProperty);
            set => SetValue(BatteryChargeColorProperty, value);
        }

        public Color GridColor
        {
            get => (Color)GetValue(GridColorProperty);
            set => SetValue(GridColorProperty, value);
        }

        public Color FrameColor
        {
            get => (Color)GetValue(FrameColorProperty);
            set => SetValue(FrameColorProperty, value);
        }

        public double FrameCornerRadius
        {
            get => (double)GetValue(FrameCornerRadiusProperty);
            set => SetValue(FrameCornerRadiusProperty, value);
        }

        /// <summary>
        /// Returns the nearest shared sample index for a pointer position.
        /// The caller can display both series in one tooltip.
        /// </summary>
        public int HitTest(Point pt)
        {
            int count = Math.Max(SystemValues?.Count ?? 0,
                Math.Max(CpuPackageValues?.Count ?? 0,
                    Math.Max(BatteryChargeValues?.Count ?? 0, BatteryDischargeValues?.Count ?? 0)));
            Rect plot = GetPlotRect(ActualWidth, ActualHeight);
            if (count < 2 || plot.Width <= 0 || plot.Height <= 0
                || pt.X < plot.Left || pt.X > plot.Right
                || pt.Y < plot.Top || pt.Y > plot.Bottom)
                return -1;

            double stepX = plot.Width / (count - 1);
            double tolerance = Math.Max(stepX * 0.5, 8.0);
            int nearest = (int)Math.Round((pt.X - plot.Left) / stepX);
            nearest = Math.Max(0, Math.Min(count - 1, nearest));
            return Math.Abs(pt.X - (plot.Left + nearest * stepX)) <= tolerance ? nearest : -1;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0)
                return;

            double radius = Math.Min(FrameCornerRadius, Math.Min(w, h) / 2.0);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), radius, radius));
            if (Background != null)
                dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));

            double max = FindNiceMaximum(SystemValues, CpuPackageValues,
                BatteryChargeValues, BatteryDischargeValues);
            Rect plot = GetPlotRect(w, h);
            DrawGrid(dc, plot, max);
            DrawTimeLabels(dc, plot);
            dc.PushClip(new RectangleGeometry(plot));
            DrawSeries(dc, SystemValues, plot, max, SystemColor, fill: true, fillAlpha: 34);
            DrawSeries(dc, CpuPackageValues, plot, max, CpuPackageColor, fill: false, fillAlpha: 0);
            DrawSeries(dc, BatteryDischargeValues, plot, max, BatteryColor, fill: false, fillAlpha: 0);
            DrawSeries(dc, BatteryChargeValues, plot, max, BatteryChargeColor, fill: false, fillAlpha: 0);
            dc.Pop();
            dc.Pop();

            var framePen = new Pen(new SolidColorBrush(FrameColor), 1.0);
            if (framePen.CanFreeze) framePen.Freeze();
            double inset = framePen.Thickness / 2.0;
            dc.DrawRoundedRectangle(null, framePen,
                new Rect(inset, inset, Math.Max(0, w - inset * 2), Math.Max(0, h - inset * 2)),
                radius, radius);
        }

        private Rect GetPlotRect(double w, double h)
        {
            const double left = 42;
            const double right = 6;
            const double top = 6;
            const double bottom = 20;
            return new Rect(left, top,
                Math.Max(0, w - left - right),
                Math.Max(0, h - top - bottom));
        }

        private void DrawGrid(DrawingContext dc, Rect plot, double max)
        {
            var pen = new Pen(new SolidColorBrush(GridColor), 0.75);
            if (pen.CanFreeze) pen.Freeze();

            for (int i = 0; i <= 4; i++)
            {
                double fraction = i / 4.0;
                double y = plot.Top + plot.Height * fraction;
                dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
                double value = max * (1.0 - fraction);
                DrawLabel(dc, FormatAxisWatts(value),
                    new Point(0, y), TextAlignment.Right, verticalCenter: true);
            }

            for (int i = 1; i <= 3; i++)
            {
                double x = plot.Left + plot.Width * (i / 4.0);
                dc.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            }
        }

        private void DrawTimeLabels(DrawingContext dc, Rect plot)
        {
            int duration = Math.Max(1, HistoryDurationSeconds);
            DrawLabel(dc, duration + "秒前", new Point(plot.Left, plot.Bottom + 2),
                TextAlignment.Left, verticalCenter: false);
            DrawLabel(dc, (duration / 2) + "秒前", new Point(plot.Left + plot.Width / 2, plot.Bottom + 2),
                TextAlignment.Center, verticalCenter: false);
            DrawLabel(dc, "现在", new Point(plot.Right, plot.Bottom + 2),
                TextAlignment.Right, verticalCenter: false);
        }

        private void DrawLabel(DrawingContext dc, string text, Point point,
            TextAlignment alignment, bool verticalCenter)
        {
            var brush = new SolidColorBrush(LabelColor);
            if (brush.CanFreeze) brush.Freeze();
            var formatted = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal,
                    FontWeights.Normal, FontStretches.Normal),
                9,
                brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                TextAlignment = alignment,
            };
            double x = alignment == TextAlignment.Center ? point.X - formatted.Width / 2.0
                    : alignment == TextAlignment.Right ? point.X - formatted.Width
                    : point.X;
            double y = verticalCenter ? point.Y - formatted.Height / 2.0 : point.Y;
            dc.DrawText(formatted, new Point(x, y));
        }

        private static string FormatAxisWatts(double watts)
            => watts > 0 ? $"{watts:F0} W" : watts < 0 ? $"{watts:F0} W" : "0 W";

        private Color LabelColor
        {
            get
            {
                var brush = Application.Current?.TryFindResource("TextFillColorSecondaryBrush") as SolidColorBrush;
                if (brush != null) return brush.Color;
                return Color.FromRgb(0x88, 0x88, 0x88);
            }
        }

        private static double FindNiceMaximum(
            IReadOnlyList<double?> first,
            IReadOnlyList<double?> second,
            IReadOnlyList<double?> third,
            IReadOnlyList<double?> fourth)
        {
            double max = 0;
            FindMaximum(first, ref max);
            FindMaximum(second, ref max);
            FindMaximum(third, ref max);
            FindMaximum(fourth, ref max);

            // Round up to a readable 1/2/5 × 10ⁿ axis and keep a non-zero range
            // for idle systems. This prevents a single tiny reading from filling
            // the entire chart.
            max = Math.Max(1.0, max * 1.15);
            double magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(max)));
            double normalized = max / magnitude;
            double step = normalized <= 1.0 ? 1.0
                        : normalized <= 2.0 ? 2.0
                        : normalized <= 5.0 ? 5.0 : 10.0;
            return step * magnitude;
        }

        private static void FindMaximum(IReadOnlyList<double?> values, ref double max)
        {
            if (values == null) return;
            for (int i = 0; i < values.Count; i++)
                if (values[i].HasValue && !double.IsNaN(values[i].Value) && !double.IsInfinity(values[i].Value))
                    max = Math.Max(max, Math.Abs(values[i].Value));
        }

        private static void DrawSeries(
            DrawingContext dc,
            IReadOnlyList<double?> values,
            Rect plot,
            double max,
            Color color,
            bool fill,
            byte fillAlpha)
        {
            if (values == null || values.Count < 2) return;

            double stepX = plot.Width / (values.Count - 1);
            var linePen = new Pen(new SolidColorBrush(color), 1.7);
            if (linePen.CanFreeze) linePen.Freeze();

            var fillBrush = new SolidColorBrush(Color.FromArgb(fillAlpha, color.R, color.G, color.B));
            if (fillBrush.CanFreeze) fillBrush.Freeze();

            int segmentStart = -1;
            for (int i = 0; i <= values.Count; i++)
            {
                bool valid = i < values.Count && values[i].HasValue
                    && !double.IsNaN(values[i].Value) && !double.IsInfinity(values[i].Value);
                if (valid && segmentStart < 0)
                    segmentStart = i;

                if ((!valid || i == values.Count) && segmentStart >= 0)
                {
                    int segmentEnd = i - 1;
                    if (segmentEnd > segmentStart)
                        DrawSegment(dc, values, segmentStart, segmentEnd, stepX, plot, max, linePen,
                            fill ? fillBrush : null);
                    else
                    {
                        Point point = PointAt(values, segmentStart, stepX, plot, max);
                        double x = point.X;
                        double y = point.Y;
                        dc.DrawEllipse(new SolidColorBrush(color), null, new Point(x, y), 1.5, 1.5);
                    }
                    segmentStart = -1;
                }
            }
        }

        private static void DrawSegment(
            DrawingContext dc,
            IReadOnlyList<double?> values,
            int start,
            int end,
            double stepX,
            Rect plot,
            double max,
            Pen linePen,
            Brush fillBrush)
        {
            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(PointAt(values, start, stepX, plot, max), false, false);
                for (int i = start + 1; i <= end; i++)
                    ctx.LineTo(PointAt(values, i, stepX, plot, max), true, false);
            }
            if (line.CanFreeze) line.Freeze();

            if (fillBrush != null)
            {
                var area = new StreamGeometry();
                using (var ctx = area.Open())
                {
                    double zeroY = plot.Bottom;
                    ctx.BeginFigure(new Point(plot.Left + start * stepX, zeroY), true, true);
                    ctx.LineTo(PointAt(values, start, stepX, plot, max), true, false);
                    for (int i = start + 1; i <= end; i++)
                        ctx.LineTo(PointAt(values, i, stepX, plot, max), true, false);
                    ctx.LineTo(new Point(plot.Left + end * stepX, zeroY), true, false);
                }
                if (area.CanFreeze) area.Freeze();
                dc.DrawGeometry(fillBrush, null, area);
            }
            dc.DrawGeometry(null, linePen, line);
        }

        private static Point PointAt(IReadOnlyList<double?> values, int index, double stepX, Rect plot, double max)
        {
            double watts = Math.Max(0, Math.Min(max, values[index].GetValueOrDefault()));
            return new Point(plot.Left + index * stepX, plot.Bottom - plot.Height * watts / max);
        }
    }
}
