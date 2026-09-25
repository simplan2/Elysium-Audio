using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;

namespace ElysiumAudio.Controls
{
    public class LufsKnob : Control
    {
        // ==================== PROPIEDADES ====================

        public static readonly StyledProperty<double> ValueProperty =
            AvaloniaProperty.Register<LufsKnob, double>(
                nameof(Value),
                14.0,
                coerce: (obj, value) =>
                {
                    var knob = (LufsKnob)obj;
                    return Math.Clamp(value, knob.Minimum, knob.Maximum);
                });

        public static readonly StyledProperty<double> MinimumProperty =
            AvaloniaProperty.Register<LufsKnob, double>(nameof(Minimum), -24.0);

        public static readonly StyledProperty<double> MaximumProperty =
            AvaloniaProperty.Register<LufsKnob, double>(nameof(Maximum), -6.0);

        public static readonly StyledProperty<int> TickCountProperty =
            AvaloniaProperty.Register<LufsKnob, int>(nameof(TickCount), 19);

        public static readonly StyledProperty<double> DefaultValueProperty =
            AvaloniaProperty.Register<LufsKnob, double>(nameof(DefaultValue), -14.0);

        public double Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public double Minimum
        {
            get => GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public double Maximum
        {
            get => GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        public int TickCount
        {
            get => GetValue(TickCountProperty);
            set => SetValue(TickCountProperty, value);
        }

        public double DefaultValue
        {
            get => GetValue(DefaultValueProperty);
            set => SetValue(DefaultValueProperty, value);
        }

        // ==================== CAMPOS ====================

        private Point _lastPointerPosition;
        private bool _dragging;

        // Ángulos: 135° a 405° (270° de recorrido) — mismo convenio que WPF típico
        private const double StartAngle = 135;
        private const double SweepRange = 270;

        // ==================== BRUSHES ====================
        // Colores alineados con la paleta de Themes/Colors.axaml.

        // Pista (arco base) — BorderDefault (#2A3346)
        private static readonly IBrush TrackBrush =
            new SolidColorBrush(Color.Parse("#10141F"));

        // Arco activo — degradé AccentPrimary -> AccentSkyblue, en la dirección
        // del recorrido del knob (abajo-izquierda -> abajo-derecha), como bg_degrade.
        private static readonly IBrush ValueBrush =
            new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#4987FA"), 0.0),
                    new GradientStop(Color.Parse("#64A4FC"), 1.0),
                },
            };

        // Ticks inactivos — gris azulado apagado (TextMuted / BorderStrong)
        private static readonly IBrush TickBrush =
            new SolidColorBrush(Color.Parse("#4D5773"));

        // Ticks activos + aguja — versión clara de AccentSkyblue, visible sobre el disco
        private static readonly IBrush ActiveTickBrush =
            new SolidColorBrush(Color.Parse("#A9CCFF"));

        // Centro del knob — BgPanelAlt (#0F131C)
        private static readonly IBrush CenterBrush =
            new SolidColorBrush(Color.Parse("#0F131C"));

        // Aro exterior — BorderStrong (#37415A), sutil
        private static readonly IBrush OuterRingBrush =
            new SolidColorBrush(Color.Parse("#374159"));

        // Borde del disco central — BorderDefault (#2A3346)
        private static readonly IBrush CenterBorderBrush =
            new SolidColorBrush(Color.Parse("#2A3346"));

        // ==================== CONSTRUCTOR ESTÁTICO ====================

        static LufsKnob()
        {
            // Redibuja automáticamente cuando cambian estas propiedades
            AffectsRender<LufsKnob>(
                ValueProperty,
                MinimumProperty,
                MaximumProperty,
                TickCountProperty);
        }

        // ==================== RENDER ====================

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var size = Math.Min(Bounds.Width, Bounds.Height);

            var outerRadius = size * 0.44;
            var arcRadius = size * 0.38;

            // 1. Aro exterior sutil
            context.DrawEllipse(null, new Pen(OuterRingBrush, 1), center, outerRadius, outerRadius);

            // 2. Pista (track) — arco base
            DrawArc(context, center, arcRadius, StartAngle, SweepRange,
                    new Pen(TrackBrush, size * 0.055));

            // 3. Arco activo según el valor
            double normalized = (Value - Minimum) / (Maximum - Minimum);
            normalized = Math.Clamp(normalized, 0, 1);
            double activeAngle = SweepRange * normalized;

            if (activeAngle > 0.1)
            {
                DrawArc(context, center, arcRadius, StartAngle, activeAngle,
                        new Pen(ValueBrush, size * 0.055));
            }

            // 4. Ticks
            DrawTicks(context, center, outerRadius, normalized);

            // 5. Disco central (sin círculo pequeño interior)
            context.DrawEllipse(
                CenterBrush,
                new Pen(CenterBorderBrush, 1),
                center,
                size * 0.27,
                size * 0.27);

            // 6. Indicador (aguja) — sin el "center dot"
            double indicatorAngle = StartAngle + activeAngle;
            double radians = indicatorAngle * Math.PI / 180;

            double indicatorInnerRadius = size * 0.20;
            double indicatorOuterRadius = size * 0.34;

            var p1 = new Point(
                center.X + Math.Cos(radians) * indicatorInnerRadius,
                center.Y + Math.Sin(radians) * indicatorInnerRadius);

            var p2 = new Point(
                center.X + Math.Cos(radians) * indicatorOuterRadius,
                center.Y + Math.Sin(radians) * indicatorOuterRadius);

            context.DrawLine(
                new Pen(ActiveTickBrush, size * 0.025),
                p1, p2);

         
        }

        private void DrawTicks(DrawingContext context, Point center, double outerRadius, double normalized)
        {
            int count = Math.Max(2, TickCount);

            for (int i = 0; i < count; i++)
            {
                double position = (double)i / (count - 1);
                double angle = StartAngle + SweepRange * position;
                double radians = angle * Math.PI / 180;

                bool active = position <= normalized + 0.0001;

                double r1 = outerRadius + 4;
                double r2 = outerRadius + (active ? 12 : 9);

                var p1 = new Point(
                    center.X + Math.Cos(radians) * r1,
                    center.Y + Math.Sin(radians) * r1);

                var p2 = new Point(
                    center.X + Math.Cos(radians) * r2,
                    center.Y + Math.Sin(radians) * r2);

                context.DrawLine(
                    new Pen(
                        active ? ActiveTickBrush : TickBrush,
                        active ? 1.5 : 1),
                    p1, p2);
            }
        }

        private static void DrawArc(
            DrawingContext context,
            Point center,
            double radius,
            double startAngle,
            double sweepAngle,
            Pen pen)
        {
            if (sweepAngle <= 0) return;

            // Clamp para evitar overflow con sweepAngle grande
            if (sweepAngle > 359.99) sweepAngle = 359.99;

            var geometry = new StreamGeometry();

            using (var ctx = geometry.Open())
            {
                double startRad = startAngle * Math.PI / 180;
                var startPoint = new Point(
                    center.X + Math.Cos(startRad) * radius,
                    center.Y + Math.Sin(startRad) * radius);

                ctx.BeginFigure(startPoint, false);

                double endRad = (startAngle + sweepAngle) * Math.PI / 180;
                var endPoint = new Point(
                    center.X + Math.Cos(endRad) * radius,
                    center.Y + Math.Sin(endRad) * radius);

                ctx.ArcTo(
                    endPoint,
                    new Size(radius, radius),
                    0,
                    sweepAngle > 180,          // isLargeArc
                    SweepDirection.Clockwise,  // en Avalonia, Y crece hacia abajo
                    true);
            }

            context.DrawGeometry(null, pen, geometry);
        }

        // ==================== INTERACCIÓN ====================

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            if (e.Handled) return;

            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed) return;

            // Doble clic → reset al valor por defecto
            if (e.ClickCount == 2)
            {
                Value = DefaultValue;
                e.Handled = true;
                return;
            }

            _dragging = true;
            _lastPointerPosition = e.GetPosition(this);
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_dragging) return;

            var position = e.GetPosition(this);
            double deltaY = _lastPointerPosition.Y - position.Y;
            _lastPointerPosition = position;

            // Fine-tune con Shift
            double sensitivity = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.01 : 0.05;

            Value += deltaY * sensitivity;
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_dragging) return;

            _dragging = false;
            e.Pointer.Capture(null);
            Cursor = Cursor.Default;
            e.Handled = true;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);

            // Step = 1/100 del rango total, más natural que 0.5 fijo
            double step = (Maximum - Minimum) / 100.0;
            double fineStep = step * 0.1;

            double delta = e.Delta.Y * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? fineStep : step);
            Value += delta;
            e.Handled = true;
        }
    }
}
