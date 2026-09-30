using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PingCandidateFinder
{
    internal sealed class MainWindow : Window
    {
        private readonly Brush background = ColorBrush("#F5F5F5");
        private readonly Brush surface = ColorBrush("#FFFFFF");
        private readonly Brush text = ColorBrush("#242424");
        private readonly Brush muted = ColorBrush("#616161");
        private readonly Brush border = ColorBrush("#E1E1E1");
        private readonly Brush accent = ColorBrush("#0F6CBD");
        private readonly string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PingCandidateFinder", "settings.json");

        private TextBox networkInput, maskInput, gatewayInput, startInput, countInput;
        private Button startButton, stopButton, copySelectedButton, copyAllButton;
        private TextBlock statusText, countText, checkedText, repliedText, elapsedText, emptyText, warningText;
        private ProgressBar progressBar;
        private ListBox resultList;
        private Grid panels;
        private Border formCard, resultCard;
        private CancellationTokenSource cancellation;
        private Stopwatch watch = new Stopwatch();
        private DispatcherTimer clock;
        private bool running;
        private bool gatewayWarning;
        private int targetCount;
        private int unknownCount;
        private readonly List<string> candidates = new List<string>();

        public MainWindow()
        {
            Title = "Ping 候选 IP 查找";
            Width = 1080;
            Height = 700;
            MinWidth = 560;
            MinHeight = 440;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = background;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 14;
            Foreground = text;
            BuildUi();
            LoadSettings();
            SizeChanged += delegate { ArrangePanels(); };
            Closing += delegate { if (cancellation != null) cancellation.Cancel(); SaveSettings(); };
            clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            clock.Tick += delegate { if (running) elapsedText.Text = watch.Elapsed.TotalSeconds.ToString("0.0") + " 秒"; };
            clock.Start();
        }

        private static Brush ColorBrush(string hex)
        {
            Brush brush = (Brush)new BrushConverter().ConvertFromString(hex);
            brush.Freeze();
            return brush;
        }

        private static TextBlock Label(string value, double size, FontWeight weight, Brush color)
        {
            return new TextBlock { Text = value, FontSize = size, FontWeight = weight,
                Foreground = color, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        }

        private Border Card(UIElement child)
        {
            return new Border { Background = surface, BorderBrush = border, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(24), Child = child };
        }

        private Button MakeButton(string title, bool primary, RoutedEventHandler click)
        {
            Button button = new Button { Content = title, Height = 34, MinWidth = 96,
                Padding = new Thickness(15, 0, 15, 0), Cursor = Cursors.Hand, FontSize = 14,
                FontWeight = FontWeights.SemiBold, Background = primary ? accent : surface,
                Foreground = primary ? surface : text, BorderBrush = primary ? accent : border,
                BorderThickness = new Thickness(1), Style = ButtonStyle(primary) };
            button.Click += click;
            return button;
        }

        private Style ButtonStyle(bool primary)
        {
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(Border));
            frame.Name = "Frame";
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            frame.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            frame.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.MarginProperty, new Thickness(12, 0, 12, 0));
            frame.AppendChild(presenter);
            ControlTemplate template = new ControlTemplate(typeof(Button)) { VisualTree = frame };
            Trigger hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                primary ? ColorBrush("#115EA3") : ColorBrush("#F4F4F4"), "Frame"));
            template.Triggers.Add(hover);
            Trigger pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty,
                primary ? ColorBrush("#0A508F") : ColorBrush("#EAEAEA"), "Frame"));
            template.Triggers.Add(pressed);
            Trigger disabled = new Trigger { Property = Button.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Border.BackgroundProperty, ColorBrush("#F2F2F2"), "Frame"));
            disabled.Setters.Add(new Setter(Border.BorderBrushProperty, ColorBrush("#E4E4E4"), "Frame"));
            disabled.Setters.Add(new Setter(Button.ForegroundProperty, ColorBrush("#A0A0A0")));
            template.Triggers.Add(disabled);
            Trigger focus = new Trigger { Property = Button.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, accent, "Frame"));
            template.Triggers.Add(focus);
            Style style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Button.TemplateProperty, template));
            return style;
        }

        private Style InputStyle()
        {
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(Border));
            frame.Name = "Frame";
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            frame.SetValue(Border.BackgroundProperty, surface);
            frame.SetValue(Border.BorderBrushProperty, ColorBrush("#BDBDBD"));
            frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            FrameworkElementFactory host = new FrameworkElementFactory(typeof(ScrollViewer));
            host.Name = "PART_ContentHost";
            host.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 0, 9, 0));
            frame.AppendChild(host);
            ControlTemplate template = new ControlTemplate(typeof(TextBox)) { VisualTree = frame };
            Trigger hover = new Trigger { Property = TextBox.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, ColorBrush("#8A8A8A"), "Frame"));
            template.Triggers.Add(hover);
            Trigger focus = new Trigger { Property = TextBox.IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, accent, "Frame"));
            focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1, 1, 1, 2), "Frame"));
            template.Triggers.Add(focus);
            Trigger disabled = new Trigger { Property = TextBox.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Border.BackgroundProperty, ColorBrush("#F3F3F3"), "Frame"));
            disabled.Setters.Add(new Setter(TextBox.ForegroundProperty, ColorBrush("#9B9B9B")));
            template.Triggers.Add(disabled);
            Style style = new Style(typeof(TextBox));
            style.Setters.Add(new Setter(TextBox.TemplateProperty, template));
            return style;
        }

        private TextBox Field(Grid grid, int row, int column, string caption, string value, string hint)
        {
            StackPanel field = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 8, 0,
                column == 0 ? 8 : 0, 14) };
            field.Children.Add(Label(caption, 13, FontWeights.SemiBold, text));
            TextBox input = new TextBox { Text = value, Height = 34, Margin = new Thickness(0, 6, 0, 3),
                VerticalContentAlignment = VerticalAlignment.Center, Style = InputStyle(), FontSize = 14 };
            field.Children.Add(input);
            if (!String.IsNullOrEmpty(hint)) field.Children.Add(Label(hint, 11, FontWeights.Normal, muted));
            Grid.SetRow(field, row);
            Grid.SetColumn(field, column);
            grid.Children.Add(field);
            return input;
        }

        private void BuildUi()
        {
            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly };
            root.Children.Add(scroll);
            StackPanel page = new StackPanel { Margin = new Thickness(32, 24, 32, 24), MaxWidth = 1240 };
            scroll.Content = page;

            Grid header = new Grid { Margin = new Thickness(0, 0, 0, 20) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Border mark = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(8),
                Background = accent, Child = new TextBlock { Text = "IP", Foreground = surface,
                FontSize = 16, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center } };
            header.Children.Add(mark);
            StackPanel heading = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            heading.Children.Add(Label("Ping 候选 IP 查找", 22, FontWeights.SemiBold, text));
            heading.Children.Add(Label("输入网段，获取待确认地址", 13, FontWeights.Normal, muted));
            Grid.SetColumn(heading, 1);
            header.Children.Add(heading);
            page.Children.Add(header);

            panels = new Grid();
            panels.ColumnDefinitions.Add(new ColumnDefinition());
            panels.ColumnDefinitions.Add(new ColumnDefinition());
            panels.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panels.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.Children.Add(panels);
            formCard = BuildForm();
            resultCard = BuildResults();
            panels.Children.Add(formCard);
            panels.Children.Add(resultCard);
            ArrangePanels();

            Border caution = new Border { Background = ColorBrush("#FFF8ED"),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 16, 0, 0) };
            warningText = Label("未回应只代表待确认；分配前请按网络规则核对。",
                12, FontWeights.Normal, ColorBrush("#805400"));
            caution.Child = warningText;
            page.Children.Add(caution);

            Border status = new Border { Background = surface, BorderBrush = border,
                BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(28, 9, 28, 9) };
            statusText = Label("填写网段和候选数量后开始查找。", 12, FontWeights.Normal, muted);
            status.Child = statusText;
            Grid.SetRow(status, 1);
            root.Children.Add(status);
        }

        private Border BuildForm()
        {
            StackPanel content = new StackPanel();
            content.Children.Add(Label("扫描设置", 18, FontWeights.SemiBold, text));
            content.Children.Add(new TextBlock { Text = "找到指定数量后自动停止",
                FontSize = 12, Foreground = muted, Margin = new Thickness(0, 4, 0, 20) });
            Grid fields = new Grid();
            fields.ColumnDefinitions.Add(new ColumnDefinition());
            fields.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 3; i++) fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            networkInput = Field(fields, 0, 0, "网段 IP", "", "例如 192.168.10.0");
            maskInput = Field(fields, 0, 1, "子网掩码", "255.255.255.0", "例如 23 或 255.255.254.0");
            countInput = Field(fields, 1, 0, "候选数量", "5", "1–1000 个");
            gatewayInput = Field(fields, 1, 1, "网关（可选）", "", "不通时提醒");
            startInput = Field(fields, 2, 0, "起始 IP（可选）", "", "");
            content.Children.Add(fields);

            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 0) };
            startButton = MakeButton("开始查找", true, StartClicked);
            stopButton = MakeButton("停止", false, StopClicked);
            stopButton.IsEnabled = false;
            stopButton.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(startButton);
            actions.Children.Add(stopButton);
            content.Children.Add(actions);

            TextBlock hint = Label("/16–/30  ·  自动跳过网段、广播地址和网关",
                12, FontWeights.Normal, muted);
            hint.Margin = new Thickness(0, 18, 0, 0);
            content.Children.Add(hint);
            return Card(content);
        }

        private Border BuildResults()
        {
            StackPanel content = new StackPanel();
            Grid title = new Grid();
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            title.Children.Add(Label("待确认 IP", 18, FontWeights.SemiBold, text));
            countText = Label("0 / 5", 13, FontWeights.SemiBold, accent);
            Grid.SetColumn(countText, 1);
            title.Children.Add(countText);
            content.Children.Add(title);
            content.Children.Add(new TextBlock { Text = "连续两次 ping 未回应",
                FontSize = 12, Foreground = muted, Margin = new Thickness(0, 4, 0, 16) });

            progressBar = new ProgressBar { Height = 4, Minimum = 0, Maximum = 1,
                Foreground = accent, Background = ColorBrush("#EBEBEB"), Margin = new Thickness(0, 0, 0, 18) };
            content.Children.Add(progressBar);

            Border listContainer = new Border { BorderBrush = border, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5), Background = surface, MinHeight = 204 };
            Grid listGrid = new Grid();
            listContainer.Child = listGrid;
            resultList = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                MinHeight = 202, MaxHeight = 290, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            resultList.SelectionChanged += delegate { copySelectedButton.IsEnabled = resultList.SelectedItem != null; };
            resultList.MouseDoubleClick += delegate { CopySelected(); };
            listGrid.Children.Add(resultList);
            emptyText = Label("扫描后，候选地址会显示在这里。", 13, FontWeights.Normal, muted);
            emptyText.HorizontalAlignment = HorizontalAlignment.Center;
            emptyText.VerticalAlignment = VerticalAlignment.Center;
            emptyText.IsHitTestVisible = false;
            listGrid.Children.Add(emptyText);
            content.Children.Add(listContainer);

            StackPanel copies = new StackPanel { Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 14, 0, 0) };
            copySelectedButton = MakeButton("复制选中", false, delegate { CopySelected(); });
            copyAllButton = MakeButton("复制全部", false, delegate { CopyAll(); });
            copySelectedButton.IsEnabled = false;
            copyAllButton.IsEnabled = false;
            copyAllButton.Margin = new Thickness(8, 0, 0, 0);
            copies.Children.Add(copySelectedButton);
            copies.Children.Add(copyAllButton);
            content.Children.Add(copies);

            Grid stats = new Grid { Margin = new Thickness(0, 20, 0, 0) };
            for (int i = 0; i < 3; i++) stats.ColumnDefinitions.Add(new ColumnDefinition());
            checkedText = Metric(stats, 0, "已检查", "0");
            repliedText = Metric(stats, 1, "已回应", "0");
            elapsedText = Metric(stats, 2, "耗时", "0.0 秒");
            content.Children.Add(stats);
            return Card(content);
        }

        private TextBlock Metric(Grid grid, int column, string caption, string initial)
        {
            StackPanel panel = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 8, 0, 0, 0) };
            panel.Children.Add(Label(caption, 11, FontWeights.Normal, muted));
            TextBlock number = Label(initial, 19, FontWeights.SemiBold, text);
            number.Margin = new Thickness(0, 2, 0, 0);
            panel.Children.Add(number);
            Grid.SetColumn(panel, column);
            grid.Children.Add(panel);
            return number;
        }

        private void ArrangePanels()
        {
            if (panels == null) return;
            bool wide = ActualWidth >= 920 || (ActualWidth == 0 && Width >= 920);
            panels.ColumnDefinitions[0].Width = new GridLength(45, GridUnitType.Star);
            panels.ColumnDefinitions[1].Width = wide ? new GridLength(55, GridUnitType.Star) : new GridLength(0);
            Grid.SetRow(formCard, 0); Grid.SetColumn(formCard, 0);
            Grid.SetRow(resultCard, wide ? 0 : 1); Grid.SetColumn(resultCard, wide ? 1 : 0);
            formCard.Margin = wide ? new Thickness(0, 0, 8, 0) : new Thickness(0, 0, 0, 14);
            resultCard.Margin = wide ? new Thickness(8, 0, 0, 0) : new Thickness(0);
        }

        private void StartClicked(object sender, RoutedEventArgs e)
        {
            ScanOptions options;
            try { options = Scanner.Parse(networkInput.Text, maskInput.Text, gatewayInput.Text,
                startInput.Text, countInput.Text); }
            catch (ArgumentException error) { MessageBox.Show(this, error.Message, "输入有误", MessageBoxButton.OK,
                MessageBoxImage.Warning); return; }

            SaveSettings();
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            running = true;
            gatewayWarning = false;
            unknownCount = 0;
            targetCount = options.Count;
            candidates.Clear();
            resultList.Items.Clear();
            emptyText.Visibility = Visibility.Visible;
            emptyText.Text = "正在查找候选地址…";
            countText.Text = "0 / " + targetCount;
            checkedText.Text = "0";
            repliedText.Text = "0";
            elapsedText.Text = "0.0 秒";
            progressBar.Maximum = options.Total;
            progressBar.Value = 0;
            startButton.IsEnabled = false;
            stopButton.IsEnabled = true;
            SetInputs(false);
            statusText.Text = options.Gateway.HasValue ? "正在检查网关…" : "正在查找候选地址…";
            watch.Restart();
            Task.Run(() => RunScan(options, token));
        }

        private void RunScan(ScanOptions options, CancellationToken token)
        {
            try
            {
                if (options.Gateway.HasValue && !Scanner.GatewayResponds(options.Gateway.Value, token)
                    && !token.IsCancellationRequested)
                {
                    Dispatcher.BeginInvoke(new Action(() => {
                        gatewayWarning = true;
                        statusText.Text = "网关 " + Scanner.Format(options.Gateway.Value) +
                            " 未回应 ping；继续扫描，请额外核对候选地址。";
                    }));
                }
                ScanProgress result = token.IsCancellationRequested ? new ScanProgress { Total = options.Total } :
                    Scanner.Scan(options, token, p => Dispatcher.BeginInvoke(new Action(() => ShowProgress(p))));
                Dispatcher.BeginInvoke(new Action(() => Finish(result, token.IsCancellationRequested)));
            }
            catch (Exception error)
            {
                Dispatcher.BeginInvoke(new Action(() => {
                    running = false; watch.Stop(); startButton.IsEnabled = true; stopButton.IsEnabled = false;
                    SetInputs(true); statusText.Text = "扫描发生错误。";
                    MessageBox.Show(this, error.Message, "扫描错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }));
            }
        }

        private void ShowProgress(ScanProgress progress)
        {
            if (!running) return;
            bool firstCandidate = candidates.Count == 0 && progress.Candidates.Count > 0;
            checkedText.Text = progress.Checked.ToString();
            repliedText.Text = progress.Replied.ToString();
            unknownCount = progress.Unknown;
            progressBar.Value = Math.Min(progress.Checked, progressBar.Maximum);
            for (int i = candidates.Count; i < progress.Candidates.Count; i++)
            {
                string address = progress.Candidates[i];
                candidates.Add(address);
                Grid row = new Grid { Margin = new Thickness(4, 8, 4, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(Label(address, 14, FontWeights.SemiBold, text));
                TextBlock badge = Label("待确认", 12, FontWeights.Normal, accent);
                Grid.SetColumn(badge, 1);
                row.Children.Add(badge);
                resultList.Items.Add(new ListBoxItem { Content = row, Tag = address, Padding = new Thickness(8, 2, 8, 2) });
            }
            countText.Text = candidates.Count + " / " + targetCount;
            copyAllButton.IsEnabled = candidates.Count > 0;
            if (candidates.Count > 0) emptyText.Visibility = Visibility.Collapsed;
            if (firstCandidate && ActualWidth < 920)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => resultCard.BringIntoView()));
        }

        private void Finish(ScanProgress result, bool stopped)
        {
            if (!running) return;
            ShowProgress(result);
            running = false;
            watch.Stop();
            elapsedText.Text = watch.Elapsed.TotalSeconds.ToString("0.0") + " 秒";
            startButton.IsEnabled = true;
            stopButton.IsEnabled = false;
            SetInputs(true);
            if (candidates.Count == 0) emptyText.Text = "没有找到候选地址。";
            string message = stopped ? "已停止，找到 " + candidates.Count + " 个待确认候选。" :
                candidates.Count >= targetCount ? "已找到 " + candidates.Count + " 个待确认候选，自动停止。" :
                "已查完整个网段，找到 " + candidates.Count + " 个待确认候选。";
            if (unknownCount > 0) message += " " + unknownCount + " 个地址因网络错误无法判断。";
            if (gatewayWarning) message += " 网关未回应 ping，请额外核对。";
            statusText.Text = message;
        }

        private void StopClicked(object sender, RoutedEventArgs e)
        {
            if (cancellation != null) cancellation.Cancel();
            stopButton.IsEnabled = false;
            statusText.Text = "正在停止…";
        }

        private void SetInputs(bool enabled)
        {
            networkInput.IsEnabled = maskInput.IsEnabled = gatewayInput.IsEnabled =
                startInput.IsEnabled = countInput.IsEnabled = enabled;
        }

        private void CopySelected()
        {
            ListBoxItem item = resultList.SelectedItem as ListBoxItem;
            if (item != null) { Clipboard.SetText((string)item.Tag); statusText.Text = "已复制到剪贴板。"; }
        }

        private void CopyAll()
        {
            if (candidates.Count > 0) { Clipboard.SetText(String.Join(Environment.NewLine, candidates));
                statusText.Text = "已复制到剪贴板。"; }
        }

        private Dictionary<string, string> Values()
        {
            return new Dictionary<string, string> { { "network", networkInput.Text }, { "mask", maskInput.Text },
                { "gateway", gatewayInput.Text }, { "start", startInput.Text }, { "count", countInput.Text } };
        }

        private void SaveSettings()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                File.WriteAllText(settingsPath, new JavaScriptSerializer().Serialize(Values())); }
            catch { /* Read-only profiles should not block a scan. */ }
        }

        private void LoadSettings()
        {
            try
            {
                Dictionary<string, string> data = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(settingsPath));
                string value;
                if (data.TryGetValue("network", out value)) networkInput.Text = value;
                if (data.TryGetValue("mask", out value)) maskInput.Text = value;
                if (data.TryGetValue("gateway", out value)) gatewayInput.Text = value;
                if (data.TryGetValue("start", out value)) startInput.Text = value;
                if (data.TryGetValue("count", out value)) countInput.Text = value;
                countText.Text = "0 / " + countInput.Text;
            }
            catch { /* Defaults are enough for the first launch. */ }
        }

        internal void ShowPreviewResults()
        {
            running = true;
            targetCount = 5;
            progressBar.Maximum = 510;
            ShowProgress(new ScanProgress { Checked = 86, Replied = 81, Total = 510,
                Candidates = new List<string> { "192.168.10.24", "192.168.10.31", "192.168.10.48" } });
            running = false;
            elapsedText.Text = "3.8 秒";
            statusText.Text = "已找到 3 个待确认候选。";
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application app = new Application();
            MainWindow window = new MainWindow();
            if (args.Length >= 2 && (args[0] == "--screenshot" || args[0] == "--screenshot-results"))
            {
                if (args.Length >= 4) { window.Width = Double.Parse(args[2]); window.Height = Double.Parse(args[3]); }
                window.Loaded += delegate {
                    window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                        if (args[0] == "--screenshot-results") window.ShowPreviewResults();
                        window.UpdateLayout();
                        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                            try {
                                window.UpdateLayout();
                                RenderTargetBitmap bitmap = new RenderTargetBitmap((int)window.ActualWidth,
                                    (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                                bitmap.Render(window);
                                PngBitmapEncoder encoder = new PngBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                using (FileStream output = File.Create(args[1])) encoder.Save(output);
                            } finally { window.Close(); }
                        }));
                    }));
                };
            }
            app.Run(window);
        }
    }
}
