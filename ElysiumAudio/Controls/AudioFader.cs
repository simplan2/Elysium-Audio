using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Text;

namespace ElysiumAudio.Controls
{
    public class AudioFader : Control
    {

        public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<AudioFader, double>(nameof(Minimum), -24.0);
        public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<AudioFader, double>(nameof(Maximum), -0.1);
        public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<AudioFader, double>(nameof(Value), -12.0);

        public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
        public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
        public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

        private bool _isDragging;

        static AudioFader()
        {
            ValueProperty.Changed.AddClassHandler<AudioFader>((x, _) => x.InvalidateVisual());
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _isDragging = true;
                UpdateValueFromPosition(e.GetPosition(this).Y);
                e.Pointer.Capture(this);
                e.Handled = true;
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_isDragging)
            {
                UpdateValueFromPosition(e.GetPosition(this).Y);
                e.Handled = true;
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_isDragging)
            {
                _isDragging = false;
                e.Pointer.Capture(null);
                e.Handled = true;
            }
        }

        private void UpdateValueFromPosition(double yPosition)
        {
            if (Bounds.Height <= 0) return;

            // Mapeo seguro del porcentaje vertical
            double percentage = 1.0 - (yPosition / Bounds.Height);
            if (percentage < 0.0) percentage = 0.0;
            if (percentage > 1.0) percentage = 1.0;

            Value = Minimum + (percentage * (Maximum - Minimum));
        }

        public override void Render(DrawingContext context)
        {
            var bounds = Bounds;
            if (bounds.Height <= 0 || bounds.Width <= 0) return;

            double centerX = bounds.Width / 2;

            // Calcular porcentaje actual del fader
            double range = Maximum - Minimum;
            double percentage = range == 0 ? 0 : (Value - Minimum) / range;
            if (percentage < 0) percentage = 0;
            if (percentage > 1) percentage = 1;

            // Posición del botón en base al porcentaje
            double thumbY = bounds.Height - (percentage * bounds.Height);

            // 1. Dibujar el Carril de Fondo Oscuro (Estático)
            context.FillRectangle(new SolidColorBrush(Color.Parse("#14181F")), new Rect(centerX - 2, 0, 4, bounds.Height), 2);

            // 2. Dibujar el Tramo Inferior de Neón Azul (Dinámico)
            if (thumbY < bounds.Height)
            {
                context.FillRectangle(new SolidColorBrush(Color.Parse("#00A2FF")), new Rect(centerX - 2, thumbY, 4, bounds.Height - thumbY), 2);
            }

            // 3. Dibujar el Botón Metálico (Thumb) usando métodos estándar de Avalonia
            var thumbRect = new Rect(centerX - 13, thumbY - 19, 26, 38);
            var gradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = {
                new GradientStop(Color.Parse("#737A85"), 0.0),
                new GradientStop(Color.Parse("#4A5059"), 0.3),
                new GradientStop(Color.Parse("#2B3036"), 0.7),
                new GradientStop(Color.Parse("#1C1F24"), 1.0)
            }
            };

            // Base redonda del botón metálico
            context.FillRectangle(gradient, thumbRect, 5);
            // Borde negro del botón
            context.DrawRectangle(new Pen(Brushes.Black, 1), thumbRect, 5);

            // Brillo superior blanco sutil
            context.FillRectangle(new SolidColorBrush(Color.Parse("#55FFFFFF")), new Rect(centerX - 12, thumbY - 18, 24, 1), 0);

            // Línea central indicadora azul brillante
            context.FillRectangle(new SolidColorBrush(Color.Parse("#00A2FF")), new Rect(centerX - 12, thumbY - 1, 24, 2), 0);
        }
    }
}
