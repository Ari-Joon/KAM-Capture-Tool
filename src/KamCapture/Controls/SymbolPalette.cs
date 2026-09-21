using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KamCapture.Editor;

namespace KamCapture.Controls
{
    /// <summary>
    /// One button that drops down the whole symbol table. Everything lives
    /// behind a single control so the toolbar keeps its depth without turning
    /// into a wall of icons.
    /// </summary>
    public sealed class SymbolPaletteButton : Button
    {
        private readonly Border _chip;
        private Popup? _popup;

        public event Action<string>? SymbolPicked;

        private string _symbol = "arrow-right";
        public string Symbol
        {
            get => _symbol;
            set
            {
                _symbol = value;
                _chip.Child = Glyph(SymbolCatalog.Get(value), 20, Brushes.White);
                ToolTip = "Symbol: " + SymbolCatalog.Get(value).Label + "  —  click to choose another";
            }
        }

        public SymbolPaletteButton()
        {
            Height = 30;
            Padding = new Thickness(7, 0, 7, 0);

            _chip = new Border { VerticalAlignment = VerticalAlignment.Center };

            var caret = new Path
            {
                Data = Geometry.Parse("M 0,0 L 4.5,5 L 9,0"),
                Stroke = new SolidColorBrush(Color.FromRgb(0x98, 0xA4, 0xBA)),
                StrokeThickness = 1.6,
                Margin = new Thickness(8, 2, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_chip);
            row.Children.Add(caret);
            Content = row;

            Symbol = _symbol;
            Click += (_, _) => Toggle();
        }

        private static UIElement Glyph(SymbolDef def, double size, Brush brush)
        {
            var path = new Path
            {
                Data = def.Geometry,
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };

            if (def.StrokeOnly)
            {
                path.Stroke = brush;
                path.StrokeThickness = 9;   // in the 100-unit authoring space
            }
            else
            {
                path.Fill = brush;
            }
            return path;
        }

        private void Toggle()
        {
            if (_popup is { IsOpen: true }) { _popup.IsOpen = false; return; }
            _popup ??= BuildPopup();
            _popup.IsOpen = true;
        }

        private Popup BuildPopup()
        {
            var stack = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };

            var title = new TextBlock
            {
                Text = "Symbols",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF2)),
                Margin = new Thickness(0, 0, 0, 2)
            };
            stack.Children.Add(title);

            var sub = new TextBlock
            {
                Text = "Pick one, then drag on the board to size it. Click once for a default size.",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x98, 0xA4, 0xBA)),
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 330
            };
            stack.Children.Add(sub);

            foreach (var group in SymbolCatalog.All.GroupBy(s => s.Group))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = group.Key,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x98, 0xA4, 0xBA)),
                    Margin = new Thickness(0, 8, 0, 4)
                });

                var wrap = new WrapPanel { MaxWidth = 336 };
                foreach (var def in group)
                    wrap.Children.Add(Cell(def));
                stack.Children.Add(wrap);
            }

            var shell = new Border
            {
                Background = (Brush)(Application.Current.TryFindResource("Panel") ?? Brushes.DimGray),
                BorderBrush = (Brush)(Application.Current.TryFindResource("Edge") ?? Brushes.Gray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = stack,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.5, Color = Colors.Black }
            };

            return new Popup
            {
                Placement = PlacementMode.Bottom,
                PlacementTarget = this,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                Child = shell
            };
        }

        private Border Cell(SymbolDef def)
        {
            var host = new Border
            {
                Width = 40, Height = 40,
                Margin = new Thickness(0, 0, 4, 4),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = def.Label,
                Child = Glyph(def, 22, new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF2)))
            };

            var idle = Brushes.Transparent;
            var hover = new SolidColorBrush(Color.FromRgb(0x23, 0x2B, 0x3A));
            var accent = new SolidColorBrush(Color.FromRgb(0x4A, 0x7C, 0xFF));

            void Refresh() => host.BorderBrush = def.Name == _symbol ? accent : Brushes.Transparent;
            Refresh();

            host.MouseEnter += (_, _) => host.Background = hover;
            host.MouseLeave += (_, _) => host.Background = idle;
            host.MouseLeftButtonUp += (_, _) =>
            {
                Symbol = def.Name;
                SymbolPicked?.Invoke(def.Name);
                if (_popup != null) _popup.IsOpen = false;
            };

            return host;
        }
    }
}
