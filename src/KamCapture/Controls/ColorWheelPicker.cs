using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KamCapture.Editor;

namespace KamCapture.Controls
{
    /// <summary>
    /// An HSV colour wheel: hue around the rim, saturation towards the centre,
    /// brightness on the slider. Built in code so it can be dropped into a
    /// popup anywhere a colour is needed.
    /// </summary>
    public sealed class ColorWheelPicker : UserControl
    {
        private readonly WheelSurface _wheel;
        private readonly Slider _value;
        private readonly Slider _alpha;
        private readonly Border _preview;
        private readonly TextBox _hex;
        private readonly WrapPanel _recent;
        private bool _suppress;

        public event Action<Color>? ColorChanged;

        private static readonly List<Color> RecentColors = new();

        private Color _color = Colors.Red;
        public Color Color
        {
            get => _color;
            set
            {
                if (_color == value) return;
                _color = value;
                SyncFromColor();
                ColorChanged?.Invoke(_color);
            }
        }

        public bool ShowAlpha
        {
            get => _alpha.Visibility == Visibility.Visible;
            set => _alpha.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        public ColorWheelPicker()
        {
            Width = 244;
            Background = (Brush)(Application.Current.TryFindResource("Panel") ?? Brushes.DimGray);

            var root = new StackPanel { Margin = new Thickness(12) };

            _wheel = new WheelSurface { Width = 190, Height = 190, HorizontalAlignment = HorizontalAlignment.Center };
            _wheel.Picked += (h, s) =>
            {
                var c = FromHsv(h, s, _value.Value, (byte)_alpha.Value);
                SetColorInternal(c, fromWheel: true);
            };
            root.Children.Add(_wheel);

            root.Children.Add(MakeLabel("Brightness"));
            _value = MakeSlider(0, 1, 1);
            _value.ValueChanged += (_, _) =>
            {
                if (_suppress) return;
                _wheel.Value = _value.Value;
                SetColorInternal(FromHsv(_wheel.Hue, _wheel.Sat, _value.Value, (byte)_alpha.Value), true);
            };
            root.Children.Add(_value);

            root.Children.Add(MakeLabel("Opacity"));
            _alpha = MakeSlider(0, 255, 255);
            _alpha.ValueChanged += (_, _) =>
            {
                if (_suppress) return;
                SetColorInternal(FromHsv(_wheel.Hue, _wheel.Sat, _value.Value, (byte)_alpha.Value), true);
            };
            root.Children.Add(_alpha);
            _alpha.Visibility = Visibility.Collapsed;

            var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            _preview = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x2E, 0x3C)),
                Background = new SolidColorBrush(_color)
            };
            Grid.SetColumn(_preview, 0);
            row.Children.Add(_preview);

            _hex = new TextBox { VerticalAlignment = VerticalAlignment.Center, Text = ColorUtil.ToHex(_color) };
            _hex.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                ApplyHex();
                e.Handled = true;
            };
            _hex.LostFocus += (_, _) => ApplyHex();
            Grid.SetColumn(_hex, 1);
            row.Children.Add(_hex);
            root.Children.Add(row);

            root.Children.Add(MakeLabel("Presets"));
            var presets = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            foreach (var hexCode in new[]
            {
                "#E5342A", "#FF8A00", "#FFD400", "#2BB673", "#00A6C0",
                "#4A7CFF", "#7A5CFF", "#FF4FA3", "#111111", "#FFFFFF"
            })
                presets.Children.Add(Swatch(ColorUtil.Parse(hexCode)));
            root.Children.Add(presets);

            root.Children.Add(MakeLabel("Recent"));
            _recent = new WrapPanel { Margin = new Thickness(0, 2, 0, 0), MinHeight = 22 };
            root.Children.Add(_recent);

            Content = new Border
            {
                Background = (Brush)(Application.Current.TryFindResource("Panel") ?? Brushes.DimGray),
                BorderBrush = (Brush)(Application.Current.TryFindResource("Edge") ?? Brushes.Gray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = root
            };

            RefreshRecent();
            SyncFromColor();
        }

        private static TextBlock MakeLabel(string text) => new()
        {
            Text = text,
            Margin = new Thickness(0, 10, 0, 2),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x98, 0xA4, 0xBA))
        };

        private static Slider MakeSlider(double min, double max, double val) => new()
        {
            Minimum = min, Maximum = max, Value = val, Height = 22
        };

        private Border Swatch(Color c)
        {
            var b = new Border
            {
                Width = 20, Height = 20, Margin = new Thickness(0, 0, 4, 4),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(c),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x3C, 0x4C)),
                Cursor = Cursors.Hand,
                ToolTip = ColorUtil.ToHex(c)
            };
            b.MouseLeftButtonDown += (_, _) => SetColorInternal(c, false);
            return b;
        }

        private void RefreshRecent()
        {
            _recent.Children.Clear();
            foreach (var c in RecentColors) _recent.Children.Add(Swatch(c));
        }

        public void RememberCurrent()
        {
            RecentColors.RemoveAll(c => c == _color);
            RecentColors.Insert(0, _color);
            while (RecentColors.Count > 10) RecentColors.RemoveAt(RecentColors.Count - 1);
            RefreshRecent();
        }

        private void ApplyHex()
        {
            try
            {
                var t = _hex.Text.Trim();
                if (!t.StartsWith("#")) t = "#" + t;
                SetColorInternal(ColorUtil.Parse(t), false);
            }
            catch { _hex.Text = ColorUtil.ToHex(_color); }
        }

        private void SetColorInternal(Color c, bool fromWheel)
        {
            _color = c;
            _preview.Background = new SolidColorBrush(c);
            _suppress = true;
            _hex.Text = ShowAlpha ? ColorUtil.ToHexA(c) : ColorUtil.ToHex(c);
            if (!fromWheel)
            {
                var (h, s, v) = ToHsv(c);
                _wheel.SetHueSat(h, s);
                _value.Value = v;
                _wheel.Value = v;
                _alpha.Value = c.A;
            }
            _suppress = false;
            ColorChanged?.Invoke(c);
        }

        private void SyncFromColor()
        {
            _suppress = true;
            var (h, s, v) = ToHsv(_color);
            _wheel.SetHueSat(h, s);
            _wheel.Value = v;
            _value.Value = v;
            _alpha.Value = _color.A;
            _preview.Background = new SolidColorBrush(_color);
            _hex.Text = ShowAlpha ? ColorUtil.ToHexA(_color) : ColorUtil.ToHex(_color);
            _suppress = false;
        }

        // ---------------- colour maths ----------------

        public static Color FromHsv(double h, double s, double v, byte a = 255)
        {
            h = ((h % 360) + 360) % 360;
            s = Math.Clamp(s, 0, 1);
            v = Math.Clamp(v, 0, 1);

            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;

            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return Color.FromArgb(a,
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        public static (double H, double S, double V) ToHsv(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;

            double h = 0;
            if (d > 0.00001)
            {
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * (((b - r) / d) + 2);
                else h = 60 * (((r - g) / d) + 4);
            }
            if (h < 0) h += 360;

            double s = max <= 0 ? 0 : d / max;
            return (h, s, max);
        }

        /// <summary>The wheel itself: an HSV disc redrawn when brightness moves.</summary>
        private sealed class WheelSurface : FrameworkElement
        {
            private WriteableBitmap? _bmp;
            private double _value = 1.0;
            private bool _dragging;

            public double Hue { get; private set; }
            public double Sat { get; private set; }

            public event Action<double, double>? Picked;

            public double Value
            {
                get => _value;
                set
                {
                    if (Math.Abs(_value - value) < 0.002) return;
                    _value = value;
                    _bmp = null;
                    InvalidateVisual();
                }
            }

            public void SetHueSat(double h, double s)
            {
                Hue = h; Sat = s;
                InvalidateVisual();
            }

            private const int Res = 190;

            private void Build()
            {
                var bmp = new WriteableBitmap(Res, Res, 96, 96, PixelFormats.Bgra32, null);
                int stride = Res * 4;
                var px = new byte[stride * Res];
                double radius = Res / 2.0;

                for (int y = 0; y < Res; y++)
                {
                    double dy = y - radius + 0.5;
                    for (int x = 0; x < Res; x++)
                    {
                        double dx = x - radius + 0.5;
                        double r = Math.Sqrt(dx * dx + dy * dy);
                        int o = y * stride + x * 4;

                        if (r > radius)
                        {
                            px[o + 3] = 0;
                            continue;
                        }

                        double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
                        if (angle < 0) angle += 360;
                        var c = FromHsv(angle, Math.Min(1, r / radius), _value);

                        // Feather the rim so the disc does not alias into a saw edge.
                        double edge = radius - r;
                        byte a = edge >= 1.2 ? (byte)255 : (byte)Math.Clamp(edge / 1.2 * 255, 0, 255);

                        px[o] = (byte)(c.B * a / 255);
                        px[o + 1] = (byte)(c.G * a / 255);
                        px[o + 2] = (byte)(c.R * a / 255);
                        px[o + 3] = a;
                    }
                }

                bmp.WritePixels(new Int32Rect(0, 0, Res, Res), px, stride, 0);
                bmp.Freeze();
                _bmp = bmp;
            }

            protected override void OnRender(DrawingContext dc)
            {
                if (_bmp == null) Build();
                double size = Math.Min(ActualWidth, ActualHeight);
                var box = new Rect((ActualWidth - size) / 2, (ActualHeight - size) / 2, size, size);
                dc.DrawImage(_bmp, box);

                double radius = size / 2;
                double cx = box.X + radius, cy = box.Y + radius;
                double rr = Sat * radius;
                double a = Hue * Math.PI / 180;
                var p = new Point(cx + Math.Cos(a) * rr, cy + Math.Sin(a) * rr);

                var outer = new Pen(Brushes.White, 2.2);
                var inner = new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1);
                dc.DrawEllipse(null, outer, p, 7, 7);
                dc.DrawEllipse(null, inner, p, 8.6, 8.6);
            }

            private void Pick(Point p)
            {
                double size = Math.Min(ActualWidth, ActualHeight);
                double radius = size / 2;
                double cx = (ActualWidth - size) / 2 + radius, cy = (ActualHeight - size) / 2 + radius;

                double dx = p.X - cx, dy = p.Y - cy;
                double r = Math.Sqrt(dx * dx + dy * dy);
                double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
                if (angle < 0) angle += 360;

                Hue = angle;
                Sat = Math.Min(1, r / radius);
                InvalidateVisual();
                Picked?.Invoke(Hue, Sat);
            }

            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                _dragging = true;
                CaptureMouse();
                Pick(e.GetPosition(this));
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                if (_dragging && e.LeftButton == MouseButtonState.Pressed)
                    Pick(e.GetPosition(this));
            }

            protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
            {
                _dragging = false;
                if (IsMouseCaptured) ReleaseMouseCapture();
            }
        }
    }

    /// <summary>A button that shows its colour and opens the wheel when clicked.</summary>
    public sealed class ColorButton : Button
    {
        private readonly Border _chip;
        private Popup? _popup;
        private ColorWheelPicker? _picker;

        public event Action<Color>? ColorChanged;

        private Color _color = Colors.Red;
        public Color Color
        {
            get => _color;
            set
            {
                _color = value;
                _chip.Background = new SolidColorBrush(value);
                ToolTip = ColorUtil.ToHex(value);
            }
        }

        public bool ShowAlpha { get; set; }

        public ColorButton()
        {
            Width = 34; Height = 30;
            Padding = new Thickness(0);
            _chip = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(_color),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x3C, 0x4C)),
                Width = 20, Height = 18
            };
            Content = _chip;
            Click += (_, _) => Toggle();
        }

        private void Toggle()
        {
            if (_popup is { IsOpen: true }) { _popup.IsOpen = false; return; }

            _picker ??= new ColorWheelPicker();
            _picker.ShowAlpha = ShowAlpha;
            _picker.Color = _color;
            _picker.ColorChanged -= OnPicked;
            _picker.ColorChanged += OnPicked;

            _popup ??= new Popup
            {
                Placement = PlacementMode.Bottom,
                PlacementTarget = this,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                Child = _picker
            };
            _popup.Closed -= OnClosed;
            _popup.Closed += OnClosed;
            _popup.IsOpen = true;
        }

        private void OnClosed(object? sender, EventArgs e) => _picker?.RememberCurrent();

        private void OnPicked(Color c)
        {
            Color = c;
            ColorChanged?.Invoke(c);
        }
    }
}
