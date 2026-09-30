using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PingCandidateFinder;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PingCandidateFinder.WinUI;

public sealed partial class MainWindow : Window
{
    private const int MaxVisibleCandidates = 1000;
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PingCandidateFinder", "settings.json");
    private readonly Stopwatch _watch = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly SemaphoreSlim _copyLock = new(1, 1);
    private readonly List<string> _allCandidates = new();
    private readonly List<string> _allReplied = new();
    private readonly List<string> _allUnknown = new();
    private CancellationTokenSource? _cancel;
    private bool _running;
    private bool _gatewayWarning;
    private bool _fullScan;
    private int _targetCount;
    private int _unknownCount;
    private int _generation;
    private bool _previewMode;
    private IntPtr _largeIcon;
    private IntPtr _smallIcon;

    public MainWindow()
    {
        InitializeComponent();
        SetWindowIcon();
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1080, 700));
        SizeChanged += (_, args) => ArrangePanels(args.Size.Width);
        Closed += (_, _) =>
        {
            _cancel?.Cancel();
            if (!_previewMode) SaveSettings();
            if (_smallIcon != IntPtr.Zero) DestroyIcon(_smallIcon);
            if (_largeIcon != IntPtr.Zero) DestroyIcon(_largeIcon);
        };
        _clock.Tick += (_, _) => { if (_running) ElapsedText.Text = $"{_watch.Elapsed.TotalSeconds:0.0} 秒"; };
        _clock.Start();
        LoadSettings();
        ScanModeInput.SelectionChanged += (_, _) => UpdateScanMode();
        ResultViewInput.SelectionChanged += (_, _) => UpdateResultView(true);
        UpdateScanMode();
    }

    private void SetWindowIcon()
    {
        string? executable = Environment.ProcessPath;
        if (executable is null || ExtractIconEx(executable, 0, out _largeIcon, out _smallIcon, 1) == 0)
            return;

        IntPtr icon = _smallIcon != IntPtr.Zero ? _smallIcon : _largeIcon;
        AppWindow.SetIcon(Microsoft.UI.Win32Interop.GetIconIdFromIcon(icon));
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    internal void SchedulePreview(string path, bool results, bool fullScan = false)
    {
        _previewMode = true;
        Root.Loaded += async (_, _) =>
        {
            try
            {
                Root.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 243, 243, 243));
                await Task.Delay(500);
                if (results)
                {
                    NetworkInput.Text = "192.168.10.0";
                    MaskInput.Text = "255.255.254.0";
                    GatewayInput.Text = "192.168.10.1";
                    StartInput.Text = "";
                    CountInput.Text = "5";
                    if (fullScan) ScanModeInput.SelectedIndex = 1;
                    _running = true;
                    _fullScan = fullScan;
                    ResultViewInput.Visibility = fullScan ? Visibility.Visible : Visibility.Collapsed;
                    _targetCount = 5;
                    ScanProgressBar.Maximum = 509;
                    List<string> previewCandidates = fullScan
                        ? Enumerable.Range(24, 102).Select(i => $"192.168.10.{i}").ToList()
                        : new List<string> { "192.168.10.24", "192.168.10.31", "192.168.10.48" };
                    ShowProgress(new ScanProgress
                    {
                        Checked = fullScan ? 509 : 86,
                        Replied = fullScan ? 404 : 81,
                        Unknown = fullScan ? 3 : 2,
                        Total = 509,
                        CandidateCount = previewCandidates.Count,
                        Candidates = previewCandidates
                    });
                    _running = false;
                    ElapsedText.Text = fullScan ? "283.2 秒" : "3.8 秒";
                    StatusText.Text = fullScan
                        ? "全量扫描完成：已检查 509/509，已回应 404，疑似空闲（待确认）102，无法判断 3。"
                        : "已找到 3 个待确认候选。";
                }
                await Task.Delay(150);
                RenderTargetBitmap bitmap = new();
                await bitmap.RenderAsync(Root.ActualWidth < 920
                    ? results ? ResultsCard : FormCard
                    : Root);
                IBuffer buffer = await bitmap.GetPixelsAsync();
                byte[] pixels = new byte[buffer.Length];
                using (DataReader pixelReader = DataReader.FromBuffer(buffer)) pixelReader.ReadBytes(pixels);
                using InMemoryRandomAccessStream output = new();
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
                await encoder.FlushAsync();
                output.Seek(0);
                byte[] png = new byte[output.Size];
                using (DataReader reader = new(output))
                {
                    await reader.LoadAsync((uint)output.Size);
                    reader.ReadBytes(png);
                }
                File.WriteAllBytes(path, png);
            }
            catch (Exception error) { App.Log(error); }
            finally { Close(); }
        };
    }

    private void ArrangePanels(double width)
    {
        bool wide = width >= 920;
        Panels.ColumnDefinitions[0].Width = new GridLength(wide ? 45 : 1, GridUnitType.Star);
        Panels.ColumnDefinitions[1].Width = wide ? new GridLength(55, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(FormCard, 0);
        Grid.SetColumn(FormCard, 0);
        Grid.SetRow(ResultsCard, wide ? 0 : 1);
        Grid.SetColumn(ResultsCard, wide ? 1 : 0);
    }

    private async void StartClicked(object sender, RoutedEventArgs e)
    {
        ScanOptions options;
        try
        {
            options = Scanner.Parse(NetworkInput.Text, MaskInput.Text, GatewayInput.Text,
                StartInput.Text, CountInput.Text, ScanModeInput.SelectedIndex == 1);
        }
        catch (ArgumentException error)
        {
            await new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "输入有误",
                Content = error.Message,
                CloseButtonText = "知道了"
            }.ShowAsync();
            return;
        }

        SaveSettings();
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        CancellationToken token = _cancel.Token;
        int generation = ++_generation;
        _running = true;
        _gatewayWarning = false;
        _unknownCount = 0;
        _fullScan = options.FullScan;
        _targetCount = options.Count;
        _allCandidates.Clear();
        _allReplied.Clear();
        _allUnknown.Clear();
        CandidateList.Items.Clear();
        ResultViewInput.Visibility = _fullScan ? Visibility.Visible : Visibility.Collapsed;
        ResultViewInput.SelectedIndex = 0;
        UpdateResultView(false);
        CheckedText.Text = $"0 / {options.Total}";
        RepliedText.Text = UnansweredText.Text = UnknownText.Text = "0";
        ElapsedText.Text = "0.0 秒";
        ListLimitText.Visibility = Visibility.Collapsed;
        ScanProgressBar.Maximum = options.Total;
        ScanProgressBar.Value = 0;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        CopySelectedButton.IsEnabled = CopyAllButton.IsEnabled = false;
        SetInputs(false);
        StatusText.Text = options.Gateway.HasValue ? "正在检查网关…"
            : _fullScan ? "正在扫描整个网段…" : "正在查找候选地址…";
        _watch.Restart();
        await Task.Run(() => RunScan(options, token, generation));
    }

    private void RunScan(ScanOptions options, CancellationToken token, int generation)
    {
        try
        {
            if (options.Gateway.HasValue && !Scanner.GatewayResponds(options.Gateway.Value, token)
                && !token.IsCancellationRequested)
            {
                string gateway = Scanner.Format(options.Gateway.Value);
                Dispatch(generation, () =>
                {
                    _gatewayWarning = true;
                    StatusText.Text = $"网关 {gateway} 未回应 ping；继续扫描，请额外核对候选地址。";
                });
            }

            ScanProgress result = token.IsCancellationRequested
                ? new ScanProgress { Total = options.Total }
                : Scanner.Scan(options, token, p => Dispatch(generation, () => ShowProgress(p)));
            Dispatch(generation, () => Finish(result, token.IsCancellationRequested));
        }
        catch (Exception error)
        {
            Dispatch(generation, () =>
            {
                _running = false;
                _watch.Stop();
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                SetInputs(true);
                StatusText.Text = "扫描发生错误：" + error.Message;
            });
        }
    }

    private void Dispatch(int generation, Action action)
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            if (generation == _generation) action();
        });
    }

    private void ShowProgress(ScanProgress progress)
    {
        if (!_running) return;
        bool firstCandidate = _allCandidates.Count == 0 && progress.Candidates.Count > 0
            && (!_fullScan || ResultViewInput.SelectedIndex == 0);
        CheckedText.Text = $"{progress.Checked} / {progress.Total}";
        RepliedText.Text = progress.Replied.ToString();
        UnansweredText.Text = progress.CandidateCount.ToString();
        UnknownText.Text = progress.Unknown.ToString();
        _unknownCount = progress.Unknown;
        ScanProgressBar.Value = Math.Min(progress.Checked, ScanProgressBar.Maximum);
        _allCandidates.AddRange(progress.Candidates);
        _allReplied.AddRange(progress.RepliedAddresses);
        _allUnknown.AddRange(progress.UnknownAddresses);
        IReadOnlyList<string> newVisible = !_fullScan || ResultViewInput.SelectedIndex == 0
            ? progress.Candidates
            : ResultViewInput.SelectedIndex == 1 ? progress.RepliedAddresses : progress.UnknownAddresses;
        foreach (string address in newVisible)
            if (CandidateList.Items.Count < MaxVisibleCandidates) CandidateList.Items.Add(address);
        UpdateResultView(false);
        if (firstCandidate && Root.ActualWidth < 920)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                double relativeTop = ResultsCard.TransformToVisual(PageScroll)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                PageScroll.ChangeView(null, Math.Max(0, PageScroll.VerticalOffset + relativeTop - 12), null, true);
            });
    }

    private void Finish(ScanProgress result, bool stopped)
    {
        if (!_running) return;
        ShowProgress(result.CloneSince(_allCandidates.Count, _allReplied.Count, _allUnknown.Count));
        _running = false;
        _watch.Stop();
        ElapsedText.Text = $"{_watch.Elapsed.TotalSeconds:0.0} 秒";
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        SetInputs(true);
        int found = _allCandidates.Count;
        string message;
        if (_fullScan)
        {
            message = stopped ? "全量扫描已停止，以下为已检查部分：" : "全量扫描完成：";
            message += $"已检查 {result.Checked}/{result.Total}，已回应 {result.Replied}，疑似空闲（待确认）{found}，无法判断 {result.Unknown}。";
            if (!stopped) message += " 未回应数量仅供初筛，分配前仍需核对。";
        }
        else
        {
            message = stopped
                ? $"已停止，找到 {found} 个待确认候选。"
                : found >= _targetCount
                    ? $"已找到 {found} 个待确认候选，自动停止。"
                    : $"已查完整个网段，找到 {found} 个待确认候选。";
            if (_unknownCount > 0) message += $" {_unknownCount} 个地址因网络错误无法判断。";
        }
        if (_gatewayWarning) message += " 网关未回应 ping，请额外核对。";
        StatusText.Text = message;
        UpdateResultView(false);
    }

    private void StopClicked(object sender, RoutedEventArgs e)
    {
        _cancel?.Cancel();
        StopButton.IsEnabled = false;
        StatusText.Text = "正在停止…";
    }

    private void SetInputs(bool enabled)
    {
        NetworkInput.IsEnabled = MaskInput.IsEnabled = GatewayInput.IsEnabled = StartInput.IsEnabled = enabled;
        ScanModeInput.IsEnabled = enabled;
        CountInput.IsEnabled = enabled && ScanModeInput.SelectedIndex != 1;
    }

    private void UpdateScanMode()
    {
        bool full = ScanModeInput.SelectedIndex == 1;
        CountInput.IsEnabled = !_running && !full;
        ScanSettingsSubtitle.Text = full ? "检查全部可扫描地址；/16 网段可能需数分钟"
            : "找到指定数量后自动停止";
        if (!_running && _allCandidates.Count == 0)
            CandidateCount.Text = full ? "0 个待确认" : $"0 / {CountInput.Text}";
    }

    private List<string> CurrentAddresses()
    {
        return !_fullScan || ResultViewInput.SelectedIndex == 0 ? _allCandidates
            : ResultViewInput.SelectedIndex == 1 ? _allReplied : _allUnknown;
    }

    private void UpdateResultView(bool refillList)
    {
        int view = _fullScan ? ResultViewInput.SelectedIndex : 0;
        ResultsTitle.Text = view switch
        {
            1 => "已回应 IP",
            2 => "无法判断 IP",
            _ => "待确认 IP"
        };
        ResultsSubtitle.Text = view switch
        {
            1 => "收到 ICMP 回应，视为在线",
            2 => "网络错误或不可达，不能推断是否空闲",
            _ => "连续两次 ping 未回应，仍需确认"
        };
        List<string> addresses = CurrentAddresses();
        if (refillList)
        {
            CandidateList.Items.Clear();
            foreach (string address in addresses.Take(MaxVisibleCandidates)) CandidateList.Items.Add(address);
        }
        CandidateCount.Text = _fullScan ? $"{addresses.Count} 个"
            : $"{addresses.Count} / {_targetCount}";
        EmptyText.Text = _running ? "正在检查地址…" : view switch
        {
            1 => "没有收到回应的地址。",
            2 => "没有无法判断的地址。",
            _ => "没有发现未回应地址。"
        };
        EmptyText.Visibility = addresses.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        CopyAllButton.IsEnabled = addresses.Count > 0;
        ListLimitText.Visibility = addresses.Count > MaxVisibleCandidates
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CandidateSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CopySelectedButton.IsEnabled = CandidateList.SelectedItem is string;
    }

    private async void CandidateDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is string address) await CopyTextAsync(address);
    }

    private async void CopySelectedClicked(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is string address) await CopyTextAsync(address);
    }

    private async void CopyAllClicked(object sender, RoutedEventArgs e)
    {
        List<string> addresses = CurrentAddresses();
        if (addresses.Count > 0) await CopyTextAsync(string.Join(Environment.NewLine, addresses));
    }

    private async Task CopyTextAsync(string value)
    {
        await _copyLock.WaitAsync();
        try
        {
            bool copied = await ClipboardCopyService.TryCopyAsync(value, text =>
            {
                DataPackage package = new();
                package.SetText(text);
                Clipboard.SetContent(package);
                Clipboard.Flush();
            }, delay => Task.Delay(delay));
            StatusText.Text = copied ? "已复制到剪贴板。" : "剪贴板暂时不可用，请稍后重试。";
        }
        catch (Exception error)
        {
            App.Log(error);
            StatusText.Text = "复制失败，请稍后重试。";
        }
        finally { _copyLock.Release(); }
    }

    private Dictionary<string, string> Settings() => new()
    {
        ["network"] = NetworkInput.Text,
        ["mask"] = MaskInput.Text,
        ["gateway"] = GatewayInput.Text,
        ["start"] = StartInput.Text,
        ["count"] = CountInput.Text,
        ["mode"] = ScanModeInput.SelectedIndex == 1 ? "full" : "quick"
    };

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Settings()));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void LoadSettings()
    {
        try
        {
            Dictionary<string, string>? data = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(_settingsPath));
            if (data is null) return;
            if (data.TryGetValue("network", out string? network)) NetworkInput.Text = network;
            if (data.TryGetValue("mask", out string? mask)) MaskInput.Text = mask;
            if (data.TryGetValue("gateway", out string? gateway)) GatewayInput.Text = gateway;
            if (data.TryGetValue("start", out string? start)) StartInput.Text = start;
            if (data.TryGetValue("count", out string? count)) CountInput.Text = count;
            if (data.TryGetValue("mode", out string? mode) && mode == "full") ScanModeInput.SelectedIndex = 1;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
    }
}
