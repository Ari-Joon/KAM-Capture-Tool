using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KamCapture.Controls;
using KamCapture.Editor;
using KamCapture.Interop;
using KamCapture.Settings;

namespace KamCapture.UI
{
    public partial class EditorWindow : Window
    {
        private readonly BoardSurface _surface;
        private readonly AppSettings _cfg;
        private ColorButton _inkColor = null!;
        private ColorButton _fillColor = null!;
        private SymbolPaletteButton _symbolPalette = null!;
        private bool _ready;
        private bool _forceChrome;
        private string? _lastSavedPath;

        // How this capture was taken, so Retake can take it the same way, and
        // every file saved from it, which a retake throws away with it.
        private readonly SnipMode _mode;
        private readonly System.Collections.Generic.List<string> _savedPaths = new();

        public EditorWindow(BitmapSource image, AppSettings cfg, SnipMode mode = SnipMode.Region, string? savedPath = null)
        {
            InitializeComponent();
            _cfg = cfg;
            _mode = mode;
            if (savedPath != null) _savedPaths.Add(savedPath);

            WindowStyling.ApplyDarkChrome(this);
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/kam-capture.ico")); } catch { }

            var doc = BoardDocument.FromCapture(image, cfg.BoardMargin);
            doc.BoardColor = cfg.BoardBackground;
            doc.ShowGrid = cfg.ShowBoardGrid;

            _surface = new BoardSurface(doc);
            _surface.Options.Color = cfg.DefaultInkColor;
            _surface.Options.Thickness = cfg.DefaultInkThickness;
            _surface.Options.FontSize = cfg.DefaultFontSize;
            BoardHost.Child = _surface;

            _surface.SelectionChanged += UpdateChrome;
            _surface.ToolChanged += UpdateChrome;
            _surface.ViewChanged += UpdateChrome;
            _surface.DocumentChanged += UpdateChrome;

            BuildColorButtons();
            BuildCombos();

            Loaded += (_, _) =>
            {
                _surface.ZoomToFit();
                SelectTool(EditTool.Select);
                _ready = true;
                UpdateChrome();
                _surface.Focus();
            };

            PreviewKeyDown += OnKey;
            Title = $"KAM Capture Tool — {image.PixelWidth} × {image.PixelHeight}";
        }

        /// <summary>
        /// Run the work normally done on Loaded. Only used by the offscreen
        /// documentation renderer, which never shows the window.
        /// </summary>
        internal void PrepareForDocShot()
        {
            _ready = true;
            _forceChrome = true;
            SelectTool(EditTool.Select);
            foreach (var item in SampleAnnotations()) _surface.Doc.Items.Add(item);
            UpdateChrome();
        }

        /// <summary>The window as it looks with one tool picked, for the layout check.</summary>
        internal void PrepareForLayoutCheck(EditTool tool)
        {
            _ready = true;
            _forceChrome = true;
            SelectTool(tool);
        }

        private System.Collections.Generic.IEnumerable<AnnItem> SampleAnnotations()
        {
            yield return new StepItem
            {
                Center = new Point(300, 320), Radius = 22, Label = "1",
                StrokeColor = "#E5342A", FillColor = "#E5342A"
            };
            yield return new TextItem
            {
                Origin = new Point(36, 300), Text = "1.  make this button bigger",
                FontSize = 20, StrokeColor = "#111111", BackgroundColor = "#FFFFFF", MaxWidth = 240
            };
            yield return new LineItem
            {
                A = new Point(332, 322), B = new Point(420, 372),
                StrokeColor = "#E5342A", Thickness = 4, ArrowEnd = true
            };
            yield return new RectItem
            {
                A = new Point(312, 342), B = new Point(472, 396),
                StrokeColor = "#E5342A", Thickness = 3
            };
            yield return new StepItem
            {
                Center = new Point(300, 430), Radius = 22, Label = "A",
                StrokeColor = "#FF8A00", FillColor = "#FF8A00"
            };
            yield return new TextItem
            {
                Origin = new Point(36, 410), Text = "A.  this row wraps badly",
                FontSize = 20, StrokeColor = "#111111", BackgroundColor = "#FFFFFF", MaxWidth = 240
            };
            yield return new SymbolItem
            {
                A = new Point(600, 300), B = new Point(672, 372),
                Symbol = "warning", Filled = true, StrokeColor = "#FFD400", Thickness = 6
            };
        }

        // ---------------- chrome construction ----------------

        private void BuildColorButtons()
        {
            _inkColor = new ColorButton { Color = ColorUtil.Parse(_cfg.DefaultInkColor) };
            _inkColor.ColorChanged += c =>
            {
                _surface.Options.Color = ColorUtil.ToHex(c);
                _surface.ApplyToSelection(i =>
                {
                    i.StrokeColor = ColorUtil.ToHex(c);
                    if (i is StepItem s && !s.Outlined) s.FillColor = ColorUtil.ToHex(c);
                });
                if (_surface.EditingItem != null)
                {
                    _surface.EditingItem.StrokeColor = ColorUtil.ToHex(c);
                    _surface.RefreshEditingStyle();
                }
            };
            HostInkColor.Content = _inkColor;

            _fillColor = new ColorButton { Color = ColorUtil.Parse(_surface.Options.ShapeFillColor), ShowAlpha = true };
            _fillColor.ColorChanged += c =>
            {
                _surface.Options.ShapeFillColor = ColorUtil.ToHexA(c);
                _surface.ApplyToSelection(i =>
                {
                    if (i is RectItem r && r.FillColor != null) r.FillColor = ColorUtil.ToHexA(c);
                    if (i is EllipseItem e && e.FillColor != null) e.FillColor = ColorUtil.ToHexA(c);
                });
            };
            HostFillColor.Content = _fillColor;

            _symbolPalette = new SymbolPaletteButton { Symbol = _surface.Options.Symbol };
            _symbolPalette.SymbolPicked += name =>
            {
                _surface.Options.Symbol = name;
                _surface.ApplyToSelection(i => { if (i is SymbolItem sy) sy.Symbol = name; });
                if (_surface.Tool != EditTool.Symbol) SelectTool(EditTool.Symbol);
            };
            HostSymbol.Content = _symbolPalette;
            ChkSymbolFilled.IsChecked = _surface.Options.SymbolFilled;
        }

        private void OnSymbolFilledToggled(object sender, RoutedEventArgs e)
        {
            _surface.Options.SymbolFilled = ChkSymbolFilled.IsChecked == true;
            _surface.ApplyToSelection(i =>
            {
                if (i is SymbolItem sy) sy.Filled = _surface.Options.SymbolFilled;
            });
        }

        private void OnRotationChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            double deg = Math.Round(e.NewValue);
            _surface.Options.SymbolRotation = deg;
            LblRotate.Text = deg.ToString("0") + "°";
            _surface.ApplyToSelection(i => { if (i is SymbolItem sy) sy.Rotation = deg; });
        }

        private void BuildCombos()
        {
            CmbStepStyle.ItemsSource = new[]
            {
                new ComboItem("1, 2, 3", StepStyle.Number),
                new ComboItem("A, B, C", StepStyle.UpperLetter),
                new ComboItem("a, b, c", StepStyle.LowerLetter),
                new ComboItem("i, ii, iii", StepStyle.LowerRoman),
                new ComboItem("I, II, III", StepStyle.UpperRoman),
            };
            CmbStepStyle.DisplayMemberPath = "Label";
            CmbStepStyle.SelectedIndex = 0;

            CmbExportScale.ItemsSource = new[]
            {
                new ComboItem("1x", 1), new ComboItem("2x", 2), new ComboItem("3x", 3), new ComboItem("4x", 4)
            };
            CmbExportScale.DisplayMemberPath = "Label";
            CmbExportScale.SelectedIndex = Math.Max(0, Math.Min(3, _cfg.ExportScale - 1));

            ChkTextBg.IsChecked = _surface.Options.TextBackground;
            SldThickness.Value = _surface.Options.Thickness;
            SldFont.Value = _surface.Options.FontSize;
            TxtFont.Text = ((int)_surface.Options.FontSize).ToString();
        }

        private sealed record ComboItem(string Label, object Value)
        {
            public override string ToString() => Label;
        }

        // ---------------- tools ----------------

        private void OnToolClick(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton { Tag: string tag } && Enum.TryParse<EditTool>(tag, out var tool))
                SelectTool(tool);
        }

        private void SelectTool(EditTool tool)
        {
            _surface.Tool = tool;
            foreach (var tb in ToolStrip.Children.OfType<ToggleButton>())
                tb.IsChecked = (tb.Tag as string) == tool.ToString();
            UpdateChrome();
            _surface.Focus();
        }

        private void UpdateChrome()
        {
            if (!IsLoaded && !_forceChrome) return;

            var tool = _surface.Tool;

            Show(GrpColour, tool is not (EditTool.Select or EditTool.MoveImage or EditTool.Crop));
            Show(GrpThickness, tool is EditTool.Pencil or EditTool.Highlighter or EditTool.Line
                or EditTool.Arrow or EditTool.Rectangle or EditTool.Ellipse or EditTool.Symbol);
            Show(GrpFont, tool is EditTool.Text or EditTool.Step);
            Show(GrpShape, tool is EditTool.Rectangle or EditTool.Ellipse);
            Show(GrpStep, tool is EditTool.Step);
            Show(GrpSymbol, tool is EditTool.Symbol);
            Show(GrpRedact, tool is EditTool.Redact);
            Show(GrpHint, tool is EditTool.Select or EditTool.MoveImage or EditTool.Crop);

            LblToolHint.Text = tool switch
            {
                EditTool.Select => "Drag empty board to marquee-select  ·  M moves the screenshot  ·  Ctrl+G groups  ·  corner handles scale",
                EditTool.MoveImage => "Drag to move the screenshot around the board  ·  hold Space to pan the view  ·  wheel zooms",
                EditTool.Crop => "Drag a rectangle over the screenshot to crop it.",
                _ => ""
            };

            // The swatch follows the tool, since each remembers its own colour.
            _inkColor.Color = ColorUtil.Parse(_surface.Options.Color);

            LblThickness.Text = ((int)Math.Round(SldThickness.Value)).ToString();
            LblNextStep.Text = "Next: " + StepItem.LabelFor(_surface.Options.StepStyle,
                _surface.Options.StepCounters[_surface.Options.StepStyle]);

            BtnUndo.IsEnabled = _surface.Undo.CanUndo;
            BtnRedo.IsEnabled = _surface.Undo.CanRedo;
            BtnGroup.IsEnabled = _surface.CanGroup;
            BtnUngroup.IsEnabled = _surface.CanUngroup;

            LblZoom.Text = $"{_surface.Zoom * 100:0}%";

            var doc = _surface.Doc;
            string sel = _surface.ImageSelected
                ? "screenshot selected"
                : _surface.Selection.Count switch
                {
                    0 => "nothing selected",
                    1 => "1 object selected",
                    var n => $"{n} objects selected"
                };

            LblStatus.Text =
                $"Screenshot {doc.SourcePixelWidth} × {doc.SourcePixelHeight} px   ·   " +
                $"Board {Math.Round(doc.BoardSize.Width)} × {Math.Round(doc.BoardSize.Height)}   ·   " +
                $"{doc.Items.Count} object{(doc.Items.Count == 1 ? "" : "s")}   ·   {sel}";
        }

        private static void Show(UIElement el, bool visible) =>
            el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        // ---------------- property handlers ----------------

        private void OnThicknessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            _surface.Options.Thickness = e.NewValue;
            _surface.ApplyToSelection(i =>
            {
                if (i is StrokeItem s) i.Thickness = s.IsHighlighter ? Math.Max(10, e.NewValue * 5) : e.NewValue;
                else if (i is not TextItem && i is not StepItem) i.Thickness = e.NewValue;
            });
            UpdateChrome();
        }

        private void OnFontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            ApplyFontSize(e.NewValue, fromSlider: true);
        }

        private void OnFontSizeTyped(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            OnFontSizeTypedCommit(sender, e);
            e.Handled = true;
        }

        private void OnFontSizeTypedCommit(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            if (double.TryParse(TxtFont.Text, out double v))
                ApplyFontSize(Math.Clamp(v, TextItem.MinFontSize, TextItem.MaxFontSize), false);
            else
                TxtFont.Text = ((int)_surface.Options.FontSize).ToString();
        }

        private void ApplyFontSize(double size, bool fromSlider)
        {
            _surface.Options.FontSize = size;
            if (fromSlider) TxtFont.Text = ((int)Math.Round(size)).ToString();
            else if (Math.Abs(SldFont.Value - size) > 0.01 && size <= SldFont.Maximum) SldFont.Value = size;

            _surface.ApplyToSelection(i =>
            {
                if (i is TextItem t) t.FontSize = size;
                if (i is StepItem s) s.Radius = Math.Max(12, size * 0.95);
            });

            if (_surface.EditingItem != null)
            {
                _surface.EditingItem.FontSize = size;
                _surface.RefreshEditingStyle();
            }
            UpdateChrome();
        }

        private void OnBoldToggled(object sender, RoutedEventArgs e)
        {
            _surface.Options.Bold = TglBold.IsChecked == true;
            _surface.ApplyToSelection(i => { if (i is TextItem t) t.Bold = _surface.Options.Bold; });
            if (_surface.EditingItem != null)
            {
                _surface.EditingItem.Bold = _surface.Options.Bold;
                _surface.RefreshEditingStyle();
            }
        }

        private void OnTextBgToggled(object sender, RoutedEventArgs e)
        {
            bool on = ChkTextBg.IsChecked == true;
            _surface.Options.TextBackground = on;
            _surface.ApplyToSelection(i =>
            {
                if (i is TextItem t) t.BackgroundColor = on ? _surface.Options.TextBackgroundColor : null;
            });
        }

        private void OnFillToggled(object sender, RoutedEventArgs e)
        {
            bool on = ChkFill.IsChecked == true;
            _surface.Options.ShapeFilled = on;
            _surface.ApplyToSelection(i =>
            {
                if (i is RectItem r) r.FillColor = on ? _surface.Options.ShapeFillColor : null;
                if (i is EllipseItem el) el.FillColor = on ? _surface.Options.ShapeFillColor : null;
            });
        }


        private void OnStepStyleChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbStepStyle.SelectedItem is ComboItem { Value: StepStyle s })
                _surface.Options.StepStyle = s;
            UpdateChrome();
        }

        private void OnResetSteps(object sender, RoutedEventArgs e)
        {
            _surface.Options.ResetSteps();
            UpdateChrome();
        }

        private void OnRedactChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            _surface.Options.RedactBlock = (int)Math.Round(e.NewValue);
            _surface.ApplyToSelection(i => { if (i is RedactItem r) r.BlockSize = _surface.Options.RedactBlock; });
        }


        private void OnExportScaleChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || CmbExportScale.SelectedItem is not ComboItem { Value: int s }) return;
            _cfg.ExportScale = s;
            _cfg.Save();
        }

        // ---------------- commands ----------------

        private void OnUndo(object sender, RoutedEventArgs e) { _surface.Undo.Undo(); UpdateChrome(); }
        private void OnRedo(object sender, RoutedEventArgs e) { _surface.Undo.Redo(); UpdateChrome(); }
        private void OnGroup(object sender, RoutedEventArgs e) => _surface.GroupSelection();
        private void OnUngroup(object sender, RoutedEventArgs e) => _surface.UngroupSelection();
        private void OnDelete(object sender, RoutedEventArgs e) => _surface.DeleteSelection();
        private void OnDuplicate(object sender, RoutedEventArgs e) => _surface.DuplicateSelection();
        private void OnSelectAll(object sender, RoutedEventArgs e) => _surface.SelectAll();

        private void OnBringFront(object sender, RoutedEventArgs e)
        {
            _surface.Undo.Push();
            _surface.Doc.BringToFront(_surface.Doc.Items.Where(_surface.Selection.Contains).ToList());
            _surface.InvalidateVisual();
        }

        private void OnSendBack(object sender, RoutedEventArgs e)
        {
            _surface.Undo.Push();
            _surface.Doc.SendToBack(_surface.Doc.Items.Where(_surface.Selection.Contains).ToList());
            _surface.InvalidateVisual();
        }

        private void OnZoomIn(object sender, RoutedEventArgs e) => _surface.SetZoom(_surface.Zoom * 1.25);
        private void OnZoomOut(object sender, RoutedEventArgs e) => _surface.SetZoom(_surface.Zoom / 1.25);
        private void OnZoom100(object sender, RoutedEventArgs e) => _surface.ZoomTo100();
        private void OnZoomFit(object sender, RoutedEventArgs e) => _surface.ZoomToFit();

        private void OnSettings(object sender, RoutedEventArgs e)
        {
            var w = new SettingsWindow(_cfg) { Owner = this };
            if (w.ShowDialog() == true)
            {
                _surface.Doc.BoardColor = _cfg.BoardBackground;
                _surface.Doc.ShowGrid = _cfg.ShowBoardGrid;
                _surface.InvalidateVisual();
            }
        }

        // ---------------- output ----------------

        private RenderTargetBitmap Flatten() => _surface.Doc.Export(_cfg.ExportScale);

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            _surface.CommitTextEdit();
            CopyToClipboard(Flatten());
            Flash("Copied to clipboard");
        }

        public static void CopyToClipboard(BitmapSource bmp)
        {
            try
            {
                var data = new DataObject();
                data.SetImage(bmp);

                // Many apps paste the PNG stream in preference to the DIB, and
                // only the PNG keeps transparency intact.
                using var ms = new MemoryStream();
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                enc.Save(ms);
                data.SetData("PNG", ms, true);

                Clipboard.SetDataObject(data, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not copy to the clipboard.\n\n" + ex.Message,
                    "KAM Capture Tool", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            _surface.CommitTextEdit();
            try
            {
                var folder = _cfg.EnsureSaveFolder();
                var suggested = Path.GetFileNameWithoutExtension(_cfg.BuildFileName(".png"));

                string path;
                if (_cfg.AskNameOnSave)
                {
                    var ask = new NameDialog(suggested, folder) { Owner = this };
                    if (ask.ShowDialog() != true) return;
                    path = ask.ChosenPath;
                }
                else
                {
                    path = NameDialog.UniquePath(Path.Combine(folder, suggested + ".png"));
                }

                SaveTo(path, Flatten());
                _lastSavedPath = path;
                _savedPaths.Add(path);
                Flash("Saved to " + path);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save.\n\n" + ex.Message, "KAM Capture Tool",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnSaveAs(object sender, RoutedEventArgs e)
        {
            _surface.CommitTextEdit();
            var path = SaveAs.AskForImage(_cfg, this);
            if (path == null) return;

            try
            {
                SaveTo(path, Flatten());
                _lastSavedPath = path;
                _savedPaths.Add(path);
                Flash("Saved as " + SaveAs.Describe(path, _cfg.SaveFolder));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save.\n\n" + ex.Message, "KAM Capture Tool",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// This capture was no good: throw it away and take it again the same
        /// way. What is drawn on it is only asked about, because that is work;
        /// files already saved from it go to the Recycle Bin, not away for good.
        /// </summary>
        private async void OnRetake(object sender, RoutedEventArgs e)
        {
            _surface.CommitTextEdit();
            if (_surface.Doc.Items.Count > 0 &&
                MessageBox.Show(this, "Throw this capture away, with what is drawn on it, and take it again?",
                    "KAM Capture Tool", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            foreach (var path in _savedPaths)
                if (Services.RecycleBin.Send(path)) SaveAs.Forget(_cfg, path);
            _savedPaths.Clear();

            await Services.CaptureController.RetakeAsync(_mode, _cfg, this);
        }

        public static void SaveTo(string path, BitmapSource bmp)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            BitmapEncoder enc = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 94 },
                ".bmp" => new BmpBitmapEncoder(),
                _ => new PngBitmapEncoder()
            };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
        }

        private void Flash(string message)
        {
            LblStatus.Text = message;
            var timer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) => { timer.Stop(); UpdateChrome(); };
            timer.Start();
        }

        /// <summary>
        /// Take another capture without leaving the annotator. This window is
        /// kept, work and all, and comes back once the new capture is taken.
        /// </summary>
        private void OnNewCapture(object sender, RoutedEventArgs e)
        {
            _surface.CommitTextEdit();
            _ = Services.CaptureController.RunAsync(_cfg.DefaultMode, _cfg);
        }

        // ---------------- keyboard ----------------

        private void OnKey(object sender, KeyEventArgs e)
        {
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            // A text field has the keyboard — the font size box, the sequence
            // list, or a text box on the board. Backspace, Delete, arrows and
            // letters belong to it: handled here, Backspace in the font size
            // box used to delete whatever shape was selected. Only the commands
            // that mean nothing inside a text field still apply.
            if (_surface.IsEditingText ||
                Keyboard.FocusedElement is TextBoxBase or ComboBox or ComboBoxItem)
            {
                if (ctrl && e.Key == Key.S) { OnSave(this, new RoutedEventArgs()); e.Handled = true; }
                else if (e.Key == Key.F12) { OnSaveAs(this, new RoutedEventArgs()); e.Handled = true; }
                else if (ctrl && shift && e.Key == Key.C) { OnCopy(this, new RoutedEventArgs()); e.Handled = true; }
                else if (ctrl && e.Key == Key.N) { OnNewCapture(this, new RoutedEventArgs()); e.Handled = true; }
                return;
            }

            if (e.Key == Key.F12) { OnSaveAs(this, new RoutedEventArgs()); e.Handled = true; return; }

            if (ctrl)
            {
                switch (e.Key)
                {
                    case Key.N: OnNewCapture(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.R: OnRetake(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.Z: _surface.Undo.Undo(); UpdateChrome(); e.Handled = true; return;
                    case Key.Y: _surface.Undo.Redo(); UpdateChrome(); e.Handled = true; return;
                    case Key.S: OnSave(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.C when shift: OnCopy(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.C: _surface.CopySelection(); e.Handled = true; return;
                    case Key.V: _surface.Paste(); e.Handled = true; return;
                    case Key.X: _surface.CutSelection(); e.Handled = true; return;
                    case Key.D: _surface.DuplicateSelection(); e.Handled = true; return;
                    case Key.A: _surface.SelectAll(); e.Handled = true; return;
                    case Key.G when shift: _surface.UngroupSelection(); e.Handled = true; return;
                    case Key.G: _surface.GroupSelection(); e.Handled = true; return;
                    case Key.D0: _surface.ZoomToFit(); e.Handled = true; return;
                    case Key.D1: _surface.ZoomTo100(); e.Handled = true; return;
                    case Key.OemPlus or Key.Add: _surface.SetZoom(_surface.Zoom * 1.25); e.Handled = true; return;
                    case Key.OemMinus or Key.Subtract: _surface.SetZoom(_surface.Zoom / 1.25); e.Handled = true; return;
                }
                return;
            }

            switch (e.Key)
            {
                case Key.Delete or Key.Back: _surface.DeleteSelection(); e.Handled = true; break;
                case Key.Escape: _surface.ClearSelection(); e.Handled = true; break;

                case Key.V: SelectTool(EditTool.Select); e.Handled = true; break;
                case Key.M: SelectTool(EditTool.MoveImage); e.Handled = true; break;
                case Key.P: SelectTool(EditTool.Pencil); e.Handled = true; break;
                case Key.K: SelectTool(EditTool.Highlighter); e.Handled = true; break;
                case Key.L: SelectTool(EditTool.Line); e.Handled = true; break;
                case Key.A: SelectTool(EditTool.Arrow); e.Handled = true; break;
                case Key.R: SelectTool(EditTool.Rectangle); e.Handled = true; break;
                case Key.O: SelectTool(EditTool.Ellipse); e.Handled = true; break;
                case Key.T: SelectTool(EditTool.Text); e.Handled = true; break;
                case Key.S: SelectTool(EditTool.Step); e.Handled = true; break;
                case Key.D: SelectTool(EditTool.Symbol); e.Handled = true; break;
                case Key.X: SelectTool(EditTool.Redact); e.Handled = true; break;
                case Key.C: SelectTool(EditTool.Crop); e.Handled = true; break;

                case Key.Left: _surface.NudgeSelection(shift ? -10 : -1, 0); e.Handled = true; break;
                case Key.Right: _surface.NudgeSelection(shift ? 10 : 1, 0); e.Handled = true; break;
                case Key.Up: _surface.NudgeSelection(0, shift ? -10 : -1); e.Handled = true; break;
                case Key.Down: _surface.NudgeSelection(0, shift ? 10 : 1); e.Handled = true; break;
            }
        }
    }
}
