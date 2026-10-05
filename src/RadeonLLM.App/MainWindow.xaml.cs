using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using RadeonLLM.Analytics;
using RadeonLLM.Core;
using RadeonLLM.Inference;
using RadeonLLM.Models;
using RadeonLLM.Runtime;

namespace RadeonLLM.App;

public sealed record ModelCard(string Id, string Title, string Detail, string ApiId, string Verdict);

public partial class MainWindow : Window
{
    readonly AppController _c;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly List<object> _chat = new();
    HttpClient? _chatHttp;
    CancellationTokenSource? _chatCts, _stressCts;
    RuntimeRelease? _pendingUpdate;
    bool _loading = true, _busy, _keyVisible;

    public MainWindow(AppController c)
    {
        _c = c;
        InitializeComponent();
        _c.StateChanged += () => Dispatcher.BeginInvoke(Refresh);
        _c.ServerFailed += f => Dispatcher.BeginInvoke(() => ShowError(f.Error, f.Repeated));
        _c.Library.Changed += () => Dispatcher.BeginInvoke(RefreshModels);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closing += (_, _) => _chatCts?.Cancel();
        LoadSettingsUi();
        RefreshModels();
        RefreshHistory();
        Refresh();
        _loading = false;
        _ = StartupChecksAsync();
    }

    async Task StartupChecksAsync()
    {
        if (_c.Runtime.IsInstalled) await _c.ProbeDeviceAsync();
        Refresh();
    }

    // ======================= HOME =======================

    void Refresh()
    {
        var gpu = _c.Gpu;
        GpuText.Text = gpu is null ? "No GPU detected" : $"{gpu.Name}   {gpu.VramGb:0} GB";
        VulkanText.Text = _c.Device is { } d
            ? $"Vulkan ✓  ({d.Name})"
            : _c.Hw.VulkanLoaderPresent ? "Vulkan loader present" + (_c.Runtime.IsInstalled ? " (no device reported by runtime)" : " (verified after Setup)")
            : "Vulkan ✗  (vulkan-1.dll not found: update your GPU driver)";

        bool installed = _c.Runtime.IsInstalled;
        SetupPanel.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;

        var m = _c.SelectedModel;
        ModelText.Text = m is null ? "No model. Open the Models tab to import or download a GGUF." : $"{m.DisplayName} {m.Quantization}";
        if (m is not null && !_busy)
        {
            var plan = _c.Server.State == ServerState.Running && _c.LastPlan is { } lp ? lp : _c.PlanFor(m);
            var p = plan.Preflight;
            PreflightText.Text = $"VRAM {Fmt.Bytes(p.TotalBytes)} / {Fmt.Bytes(p.AvailableBytes)}  ·  {plan.Config.Describe()}  ·  "
                + (p.Safe ? "✓ SAFE" : "⚠ VRAM LIMITED") + (plan.Note is null ? "" : $"\n{plan.Note}");
        }
        else PreflightText.Text = "";

        var st = _c.Server.State;
        var s = _c.Settings;
        ApiDot.Foreground = (System.Windows.Media.Brush)FindResource(st switch
        {
            ServerState.Running => "Ok", ServerState.Starting or ServerState.Recovering => "Warn", ServerState.Failed => "Accent", _ => "MutedBrush"
        });
        ApiText.Text = st switch
        {
            ServerState.Running => $"Running  {s.BindAddress}:{s.Port}" + (s.ApiEnabled ? "" : "  (API server disabled; internal port only)"),
            ServerState.Starting => "Starting... loading model",
            ServerState.Recovering => "Recovering after a crash...",
            ServerState.Failed => "Failed",
            _ => "Stopped"
        };

        RunBtn.IsEnabled = installed && m is not null && !_busy && st is not (ServerState.Starting or ServerState.Recovering);
        RunBtn.Content = st == ServerState.Running ? "STOP" : "RUN";
        if (_c.SafeMode) StatusText.Text = "Safe Mode: conservative settings in use.";
        else if (!installed) StatusText.Text = "";
        else if (m is null) StatusText.Text = "Add a model to get started.";

        var t = _c.Monitor.Latest;
        var ses = _c.Tracker.Snapshot();
        var sb = new StringBuilder();
        sb.AppendLine($"GPU       {Fmt.Opt(t?.GpuUtil, "%")}");
        sb.AppendLine($"VRAM      {(t?.VramUsedBytes is { } v ? Fmt.Bytes(v) : "n/a")}{(gpu is null ? "" : $" / {gpu.VramGb:0} GB")}");
        sb.AppendLine($"Speed     {Fmt.Opt(ses.LastGenerationTps, "tok/s", "0.0")}");
        sb.AppendLine($"Power     {Fmt.Opt(t?.PowerW, "W")}    Temp {Fmt.Opt(t?.TempC, "°C")}    Hotspot {Fmt.Opt(t?.HotspotC, "°C")}");
        sb.Append($"Clocks    core {Fmt.Opt(t?.GpuClockMhz, "MHz")}    mem {Fmt.Opt(t?.MemClockMhz, "MHz")}");
        LiveText.Text = sb.ToString();

        RefreshPerformance(ses, t);
        RefreshApiTotals();
        RefreshRuntimeText();
    }

    async void Run_Click(object sender, RoutedEventArgs e)
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
        _busy = true; RunBtn.IsEnabled = false;
        try
        {
            if (_c.Server.State == ServerState.Running)
            {
                StatusText.Text = "Stopping...";
                await _c.StopAsync();
                StatusText.Text = "";
            }
            else
            {
                StatusText.Text = "Starting server...";
                await _c.StartAsync();
                StatusText.Text = $"Server running. API: {_c.Settings.LocalEndpoint}";
            }
        }
        catch (Exception ex)
        {
            _c.Logs.App.Error("Start failed", ex);
            StatusText.Text = "";
            if (ErrorPanel.Visibility != Visibility.Visible)
                ShowError(ErrorTranslator.Translate(ex.InnerException?.Message ?? ex.Message) with { Title = ex.Message }, false);
        }
        finally { _busy = false; Refresh(); }
    }

    void ShowError(FriendlyError err, bool repeated)
    {
        ErrorTitle.Text = repeated ? "Server failed repeatedly. Possible unstable GPU configuration." : err.Title;
        ErrorCauses.Text = (err.Causes.Length > 0 ? "Possible causes:\n" + string.Join("\n", err.Causes.Select(x => "• " + x)) : "");
        ErrorActions.Children.Clear();
        var actions = repeated ? ["Safe Mode", "Open Diagnostics"] : err.Actions;
        foreach (var a in actions)
        {
            var b = new Button { Content = a };
            var action = a;
            b.Click += async (_, _) => await ErrorAction(action);
            ErrorActions.Children.Add(b);
        }
        ErrorPanel.Visibility = Visibility.Visible;
        Tabs.SelectedIndex = 0;
    }

    async Task ErrorAction(string action)
    {
        try
        {
            switch (action)
            {
                case "Retry": Run_Click(this, new RoutedEventArgs()); break;
                case "Reduce Context": _c.ReduceContext(); LoadSettingsUi(); ErrorPanel.Visibility = Visibility.Collapsed; Run_Click(this, new RoutedEventArgs()); break;
                case "Reduce GPU Offload": _c.ReduceGpuOffload(); LoadSettingsUi(); ErrorPanel.Visibility = Visibility.Collapsed; Run_Click(this, new RoutedEventArgs()); break;
                case "Safe Mode": ErrorPanel.Visibility = Visibility.Collapsed; StatusText.Text = "Restarting in Safe Mode..."; await _c.EnterSafeModeAsync(); StatusText.Text = "Safe Mode active."; break;
                case "Open Diagnostics": LogBox.SelectedIndex = 1; Tabs.SelectedIndex = 4; LoadLog(); break;
                case "Verify Model": Tabs.SelectedIndex = 1; break;
                case "Update Runtime": case "Change Port in Settings": Tabs.SelectedIndex = 4; break;
            }
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "RadeonLLM"); }
    }

    async void Setup_Click(object sender, RoutedEventArgs e)
    {
        SetupBtn.IsEnabled = false; SetupBar.Visibility = SetupStage.Visibility = Visibility.Visible;
        var prog = new Progress<(string, double)>(p => { SetupStage.Text = p.Item1; SetupBar.Value = p.Item2; });
        try { await _c.SetupRuntimeAsync(prog); }
        catch (Exception ex) { SetupStage.Text = "Setup failed: " + ex.Message; _c.Logs.App.Error("Setup failed", ex); }
        finally { SetupBtn.IsEnabled = true; Refresh(); }
    }

    void CopyEndpoint_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_c.Settings.LocalEndpoint);
        StatusText.Text = "Copied " + _c.Settings.LocalEndpoint;
    }

    void OpenChat_Click(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 2;
    void GoBenchmark_Click(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 3;

    // ======================= MODELS =======================

    void RefreshModels()
    {
        var sel = (ModelList.SelectedItem as ModelCard)?.Id;
        var gpuTotal = _c.Gpu?.VramBytes ?? 0;
        var cards = _c.Library.Models.Select(m =>
        {
            var pre = VramPreflight.Estimate(m, 8192, gpuTotal > 0 ? (long)(gpuTotal * 0.95) : long.MaxValue);
            var verdict = gpuTotal == 0 ? "" : pre.Safe ? "✓ Recommended" : pre.RecommendedContext is not null ? "⚠ Reduced context" : "✗ Too large";
            var def = m.Id == _c.Settings.DefaultModelId ? "  [default]" : "";
            return new ModelCard(m.Id, $"{m.DisplayName} {m.Quantization}{def}",
                $"Size {Fmt.Bytes(m.SizeBytes)}  ·  Est. VRAM {Fmt.Bytes(pre.TotalBytes)} @ 8K  ·  {m.Architecture}", m.Id, verdict);
        }).ToList();
        ModelList.ItemsSource = cards;
        ModelList.SelectedItem = cards.FirstOrDefault(x => x.Id == sel);
    }

    ModelCard? Selected()
    {
        if (ModelList.SelectedItem is ModelCard c) return c;
        MessageBox.Show("Select a model first.", "RadeonLLM");
        return null;
    }

    async void AddModel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "GGUF models (*.gguf)|*.gguf", Title = "Select a GGUF model" };
        if (dlg.ShowDialog() != true) return;
        var copy = MessageBox.Show("Copy the file into RadeonLLM's models folder?\n\nYes = copy (managed)\nNo = use it where it is", "Import model",
            MessageBoxButton.YesNo) == MessageBoxResult.Yes;
        ModelsBar.Visibility = Visibility.Visible; ModelsStatus.Text = "Importing...";
        try
        {
            var entry = await _c.Library.ImportAsync(dlg.FileName, copy, new Progress<double>(f => ModelsBar.Value = f));
            if (_c.Settings.DefaultModelId is null) { _c.Settings.DefaultModelId = entry.Id; _c.SettingsStore.Save(); }
            ModelsStatus.Text = $"Added {entry.Id}";
        }
        catch (Exception ex) { ModelsStatus.Text = ""; MessageBox.Show(ex.Message, "Import failed"); }
        finally { ModelsBar.Visibility = Visibility.Collapsed; }
    }

    async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        var input = DownloadDialog.Ask(this);
        if (string.IsNullOrWhiteSpace(input)) return;
        ModelsBar.Visibility = Visibility.Visible;
        try
        {
            var entry = await _c.Downloader.DownloadAsync(input, new Progress<(long done, long total)>(p =>
            {
                if (p.total > 0) ModelsBar.Value = (double)p.done / p.total;
                ModelsStatus.Text = $"Downloading {Fmt.Bytes(p.done)} / {Fmt.Bytes(p.total)}";
            }));
            if (_c.Settings.DefaultModelId is null) { _c.Settings.DefaultModelId = entry.Id; _c.SettingsStore.Save(); }
            ModelsStatus.Text = $"Downloaded {entry.Id}";
        }
        catch (Exception ex) { ModelsStatus.Text = ""; MessageBox.Show(ex.Message, "Download failed"); }
        finally { ModelsBar.Visibility = Visibility.Collapsed; }
    }

    void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } c) return;
        _c.Settings.DefaultModelId = c.Id; _c.SettingsStore.Save(); RefreshModels();
        ModelsStatus.Text = _c.Server.State == ServerState.Running ? "Default set. Restart the server to load it." : "Default set.";
    }

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } c) return;
        var name = InputDialog.Ask(this, "Rename model", "Display name (the API id stays the same):", _c.Library.Find(c.Id)?.DisplayName);
        if (!string.IsNullOrWhiteSpace(name)) _c.Library.Rename(c.Id, name);
    }

    async void VerifyModel_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } c) return;
        ModelsStatus.Text = "Verifying (hashing file)...";
        var r = await _c.Library.VerifyAsync(c.Id);
        ModelsStatus.Text = (r.Ok ? "✓ " : "✗ ") + r.Message;
    }

    void RemoveModel_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } c) return;
        var m = _c.Library.Find(c.Id);
        var warn = m?.Managed == true ? "This also deletes the model file." : "The file stays on disk.";
        if (MessageBox.Show($"Remove {c.Title}?\n{warn}", "Remove model", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (_c.Server.State == ServerState.Running && _c.LastPlan?.Config.Alias == c.Id)
        { MessageBox.Show("Stop the server first.", "RadeonLLM"); return; }
        _c.Library.Remove(c.Id);
        if (_c.Settings.DefaultModelId == c.Id) { _c.Settings.DefaultModelId = null; _c.SettingsStore.Save(); }
    }

    // ======================= CHAT =======================

    void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
        { e.Handled = true; ChatSend_Click(sender, e); }
    }

    async void ChatSend_Click(object sender, RoutedEventArgs e)
    {
        if (_chatCts is not null) { _chatCts.Cancel(); return; }
        var text = ChatInput.Text.Trim();
        if (text.Length == 0) return;
        if (_c.Server.State != ServerState.Running || _c.SelectedModel is not { } model)
        { ChatStatus.Text = "Start the server first (Home → RUN)."; return; }

        ChatInput.Clear();
        _chat.Add(new { role = "user", content = text });
        ChatLog.AppendText($"You: {text}\n\nAssistant: ");
        ChatSend.Content = "Stop";
        _chatCts = new CancellationTokenSource();
        var reply = new StringBuilder();
        var sw = Stopwatch.StartNew(); int chunks = 0;
        try
        {
            _chatHttp ??= new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var req = new HttpRequestMessage(HttpMethod.Post, _c.Settings.LocalEndpoint + "/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { model = model.Id, messages = _chat, stream = true }), Encoding.UTF8, "application/json")
            };
            if (_c.Settings.AuthRequired) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _c.Keys.GetOrCreate());
            using var resp = await _chatHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _chatCts.Token);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
            using var rd = new StreamReader(await resp.Content.ReadAsStreamAsync(_chatCts.Token));
            string? line;
            while ((line = await rd.ReadLineAsync(_chatCts.Token)) is not null)
            {
                if (!line.StartsWith("data:")) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                try
                {
                    using var d = JsonDocument.Parse(data);
                    if (d.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0
                        && ch[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var cont)
                        && cont.ValueKind == JsonValueKind.String)
                    {
                        var piece = cont.GetString()!;
                        reply.Append(piece); chunks++;
                        ChatLog.AppendText(piece); ChatLog.ScrollToEnd();
                    }
                }
                catch (JsonException) { }
            }
            ChatStatus.Text = $"{chunks} tokens in {sw.Elapsed.TotalSeconds:0.0}s ({chunks / Math.Max(0.001, sw.Elapsed.TotalSeconds):0.0} tok/s)";
        }
        catch (OperationCanceledException) { ChatStatus.Text = "Stopped."; }
        catch (Exception ex) { ChatStatus.Text = "Error: " + ex.Message; }
        finally
        {
            if (reply.Length > 0) _chat.Add(new { role = "assistant", content = reply.ToString() });
            ChatLog.AppendText("\n\n");
            _chatCts?.Dispose(); _chatCts = null; ChatSend.Content = "Send";
        }
    }

    // ======================= PERFORMANCE =======================

    void RefreshPerformance(SessionStats s, TelemetrySample? t)
    {
        var plan = _c.LastPlan;
        SessionText.Text =
            $"Requests            {s.Requests}\n" +
            $"Prompt tokens       {s.PromptTokens:N0}\n" +
            $"Generated tokens    {s.GeneratedTokens:N0}\n" +
            $"Prompt processing   {Fmt.Opt(s.PromptTps, "tok/s")}\n" +
            $"Generation          {Fmt.Opt(s.GenerationTps, "tok/s", "0.0")}  (peak {Fmt.Opt(s.PeakTps, "", "0")}, min {Fmt.Opt(s.MinTps, "", "0")} per-second)\n" +
            $"Median TTFT         {(s.MedianTtft is { } tt ? $"{tt * 1000:0} ms" : "n/a")}\n" +
            $"GPU utilisation     {Fmt.Opt(t?.GpuUtil, "%")}\n" +
            $"VRAM                {(t?.VramUsedBytes is { } v ? Fmt.Bytes(v) : "n/a")}\n" +
            $"GPU power           {Fmt.Opt(t?.PowerW, "W")}\n" +
            $"GPU temperature     {Fmt.Opt(t?.TempC, "°C")}    Hotspot {Fmt.Opt(t?.HotspotC, "°C")}\n" +
            $"Context             {(plan is null ? "n/a" : $"{plan.Config.Context / 1024}K")}\n" +
            $"Session duration    {s.Duration:mm\\:ss}";
    }

    DateTime _totalsAt;
    void RefreshApiTotals()
    {
        if (DateTime.UtcNow - _totalsAt < TimeSpan.FromSeconds(5)) return;
        _totalsAt = DateTime.UtcNow;
        try
        {
            var a = _c.Store.ApiTotalsAllTime();
            ApiTotalsText.Text =
                $"Requests            {a.Requests:N0}\nInput tokens        {a.InputTokens:N0}\nOutput tokens       {a.OutputTokens:N0}\n" +
                $"Prompt processing   {Fmt.Opt(a.PromptTps, "tok/s")}\nGeneration          {Fmt.Opt(a.GenerationTps, "tok/s", "0.0")}\n" +
                $"Median TTFT         {(a.MedianTtft is { } m ? $"{m * 1000:0} ms" : "n/a")}";
        }
        catch { }
    }

    async void Benchmark_Click(object sender, RoutedEventArgs e)
    {
        if (_c.Server.State != ServerState.Running || _c.SelectedModel is not { } m || _c.LastPlan is not { } plan)
        { BenchText.Text = "Start the server first."; return; }
        BenchBtn.IsEnabled = false;
        try
        {
            var prog = new Progress<string>(s => BenchText.Text = s);
            var r = await BenchmarkRunner.RunAsync(_c.CreateApiClient(), _c.Store, _c.Monitor, m.Id, m.Quantization, plan.Config.Context,
                _c.GpuLabel, _c.Runtime.Version, prog, default, label: string.IsNullOrWhiteSpace(BenchLabel.Text) ? null : BenchLabel.Text.Trim());
            BenchText.Text =
                $"{_c.GpuLabel}\nModel: {m.DisplayName} {m.Quantization}\n\n" +
                $"Prompt processing   {r.PromptTps:N0} tok/s\nGeneration          avg {r.GenAvg:0.0}  median {r.GenMedian:0.0}  min {r.GenMin:0.0}  max {r.GenMax:0.0} tok/s\n" +
                $"TTFT                {r.Ttft * 1000:0} ms\nVRAM (peak)         {(r.Hardware.PeakVramBytes is { } v ? Fmt.Bytes(v) : "n/a")}\n" +
                $"Power (avg)         {Fmt.Opt(r.Hardware.AvgPowerW, "W")}\nPerformance/W       {(r.PerfPerWatt is { } pw ? $"{pw:0.000} tok/s/W" : "n/a")}";
            RefreshHistory();
        }
        catch (Exception ex) { BenchText.Text = "Benchmark failed: " + ex.Message; }
        finally { BenchBtn.IsEnabled = true; }
    }

    async void Tune_Click(object sender, RoutedEventArgs e)
    {
        TuneBtn.IsEnabled = false;
        try
        {
            var (results, best) = await _c.TuneBatchAsync(new Progress<string>(s => TuneText.Text = s));
            TuneText.Text = string.Join(Environment.NewLine, results.Select(r => $"batch {r.Batch,5} / ubatch {r.UBatch,5}   prompt {r.PromptTps,7:0} tok/s   gen {r.GenTps,6:0.0} tok/s"))
                + $"{Environment.NewLine}{Environment.NewLine}Best: {best.Batch}/{best.UBatch}. Saved as Custom profile; server restored.";
            LoadSettingsUi();
        }
        catch (Exception ex) { TuneText.Text = "Tuning failed: " + ex.Message; }
        finally { TuneBtn.IsEnabled = true; }
    }

    void RefreshHistory()
    {
        try
        {
            var model = _c.SelectedModel?.Id;
            var list = _c.Store.Benchmarks(model, 6);
            if (list.Count == 0) { HistoryText.Text = "No benchmarks yet."; return; }
            var sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                sb.Append($"{b.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm}  {b.GenAvg,6:0.0} tok/s  {Fmt.Opt(b.Hardware.AvgPowerW, "W"),7}  {Fmt.Opt(b.Hardware.PeakTempC, "°C"),6}  {b.Label}");
                if (i + 1 < list.Count)
                {
                    var c = BenchmarkRunner.Compare(b, list[i + 1]);
                    sb.Append($"   vs prev: perf {c.GenDeltaPct:+0.0;-0.0}%" + (c.PowerDeltaPct is { } p ? $", power {p:+0.0;-0.0}%" : ""));
                }
                sb.AppendLine();
            }
            HistoryText.Text = sb.ToString();
        }
        catch { }
    }

    async void Stress_Click(object sender, RoutedEventArgs e)
    {
        if (_stressCts is not null) { _stressCts.Cancel(); return; }
        if (_c.Server.State != ServerState.Running || _c.SelectedModel is not { } m)
        { StressText.Text = "Start the server first."; return; }
        var minutes = int.Parse((string)((ComboBoxItem)StressDuration.SelectedItem).Tag);
        _stressCts = new CancellationTokenSource();
        StressBtn.Content = "Stop Stress Test";
        try
        {
            var prog = new Progress<(TimeSpan elapsed, long tokens, int errors)>(p =>
                StressText.Text = $"Running...  {p.elapsed:mm\\:ss} / {minutes}:00   tokens {p.tokens:N0}   errors {p.errors}");
            var r = await StressTester.RunAsync(_c.CreateApiClient(), _c.Store, _c.Monitor, _c.Server, m.Id, TimeSpan.FromMinutes(minutes), prog, _stressCts.Token);
            StressText.Text =
                $"STABILITY TEST\n\nDuration       {r.Duration:hh\\:mm\\:ss}\nTokens         {r.Tokens:N0}\n" +
                $"Average speed  {r.AvgTps:0.0} tok/s\nMinimum speed  {r.MinTps:0.0} tok/s\n" +
                $"GPU errors     {r.GpuErrors}\nRuntime errors {r.Errors + r.Crashes}\nPeak temp      {Fmt.Opt(r.Hardware.PeakTempC, "°C")}   Avg power {Fmt.Opt(r.Hardware.AvgPowerW, "W")}\n\n" +
                (r.Passed ? "PASS" : "FAIL");
        }
        catch (Exception ex) { StressText.Text = "Stress test failed: " + ex.Message; }
        finally { _stressCts?.Dispose(); _stressCts = null; StressBtn.Content = "Start Stress Test"; }
    }

    // ======================= SETTINGS =======================

    void LoadSettingsUi()
    {
        var was = _loading; _loading = true;
        var s = _c.Settings;
        ProfileBox.SelectedIndex = (int)s.Profile;
        CtxBox.Text = s.ContextSize.ToString(); NglBox.Text = s.GpuLayers.ToString(); FaBox.IsChecked = s.FlashAttention; KvBox.SelectedIndex = s.KvCacheType == "q8_0" ? 1 : 0;
        ApiEnabledBox.IsChecked = s.ApiEnabled; PortBox.Text = s.Port.ToString();
        LocalOnlyRadio.IsChecked = !s.LanAccess; LanRadio.IsChecked = s.LanAccess;
        LocalKeyBox.IsChecked = s.RequireApiKeyOnLocalhost; OfflineBox.IsChecked = s.OfflineMode;
        CustomPanel.Visibility = s.Profile == PerformanceProfile.Custom ? Visibility.Visible : Visibility.Collapsed;
        UpdateKeyBox(); UpdateLanText();
        _loading = was;
    }

    void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _c.Settings;
        s.Profile = (PerformanceProfile)Math.Max(0, ProfileBox.SelectedIndex);
        if (int.TryParse(CtxBox.Text, out var ctx) && ctx >= 512) s.ContextSize = ctx;
        if (int.TryParse(NglBox.Text, out var ngl) && ngl >= -1) s.GpuLayers = ngl;
        s.FlashAttention = FaBox.IsChecked == true;
        s.KvCacheType = KvBox.SelectedIndex == 1 ? "q8_0" : "f16";
        s.ApiEnabled = ApiEnabledBox.IsChecked == true;
        if (int.TryParse(PortBox.Text, out var port) && port is > 1023 and < 65536 && port != s.InternalPort) s.Port = port;
        s.RequireApiKeyOnLocalhost = LocalKeyBox.IsChecked == true;
        s.OfflineMode = OfflineBox.IsChecked == true;
        CustomPanel.Visibility = s.Profile == PerformanceProfile.Custom ? Visibility.Visible : Visibility.Collapsed;
        _c.SettingsStore.Save();
        PlanText.Text = _c.Server.State == ServerState.Running ? "Changes to inference or API settings apply after you stop and run the server again." : "";
        UpdateKeyBox(); UpdateLanText();
    }

    void Network_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool lan = LanRadio.IsChecked == true;
        if (lan)
        {
            var ok = MessageBox.Show(
                "Local network access exposes the inference server to every device on your network.\n\n" +
                "An API key will be required for all requests. Anyone with the key can use your GPU.\n\nEnable local network access?",
                "Enable network access", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!ok) { LocalOnlyRadio.IsChecked = true; return; }
            _c.Keys.GetOrCreate();
        }
        _c.Settings.LanAccess = lan;
        _c.SettingsStore.Save();
        UpdateKeyBox(); UpdateLanText();
        if (_c.Server.State == ServerState.Running) _ = _c.StartApiAsync();
    }

    void UpdateLanText()
    {
        var s = _c.Settings;
        LanText.Text = s.LanAccess ? $"Endpoint: http://{_c.LanAddress() ?? "192.168.x.x"}:{s.Port}/v1/   (bind 0.0.0.0, API key required)" : "Bind 127.0.0.1, no key required.";
    }

    void UpdateKeyBox()
    {
        var key = _c.Keys.Get();
        KeyBox.Text = key is null ? "(none generated)" : _keyVisible ? key : new string('•', 16);
        KeyShowBtn.Content = _keyVisible ? "Hide" : "Show";
    }

    void KeyShow_Click(object sender, RoutedEventArgs e) { _keyVisible = !_keyVisible; UpdateKeyBox(); }
    void KeyCopy_Click(object sender, RoutedEventArgs e) { Clipboard.SetText(_c.Keys.GetOrCreate()); UpdateKeyBox(); }

    void KeyRegen_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Existing clients will stop working until updated. Regenerate the API key?", "API key", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _c.Keys.Generate(); UpdateKeyBox();
        if (_c.Server.State == ServerState.Running) _ = _c.StartApiAsync();
    }

    // ---- runtime ----

    void RefreshRuntimeText()
    {
        var m = _c.Runtime.Manifest;
        RuntimeText.Text = m?.Version is null ? "Not installed"
            : $"llama.cpp {m.Version}  ·  {m.Backend} {m.Architecture}" + (_c.Runtime.CanRollback ? $"  ·  previous: {m.Previous}" : "");
    }

    async Task RtOp(string label, Func<IProgress<(string, double)>, Task> op)
    {
        if (_c.Server.State is ServerState.Running or ServerState.Starting)
        { RtStatus.Text = "Stop the server first."; return; }
        RtBar.Visibility = Visibility.Visible; RtBar.Value = 0; RtStatus.Text = label + "...";
        try
        {
            await op(new Progress<(string, double)>(p => { RtStatus.Text = p.Item1; RtBar.Value = p.Item2; }));
            await _c.ProbeDeviceAsync();
            RtStatus.Text = label + " done.";
        }
        catch (Exception ex) { RtStatus.Text = label + " failed: " + ex.Message; _c.Logs.App.Error(label + " failed", ex); }
        finally { RtBar.Visibility = Visibility.Collapsed; Refresh(); }
    }

    async void RtCheck_Click(object sender, RoutedEventArgs e)
    {
        RtStatus.Text = "Checking...";
        try
        {
            _pendingUpdate = await _c.Runtime.CheckForUpdateAsync(_c.Settings.IgnoredRuntimeVersion);
            RtStatus.Text = _pendingUpdate is null ? "Runtime is up to date." : $"New runtime available.  Current: {_c.Runtime.Version ?? "none"}  Available: {_pendingUpdate.Version}";
            RtUpdateBtn.Visibility = RtIgnoreBtn.Visibility = _pendingUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex) { RtStatus.Text = "Check failed: " + ex.Message; }
    }

    async void RtUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is not { } rel) return;
        await RtOp("Updating runtime", p => _c.Runtime.InstallAsync(rel, p));
        _pendingUpdate = null; RtUpdateBtn.Visibility = RtIgnoreBtn.Visibility = Visibility.Collapsed;
    }

    void RtIgnore_Click(object sender, RoutedEventArgs e)
    {
        _c.Settings.IgnoredRuntimeVersion = _pendingUpdate?.Version; _c.SettingsStore.Save();
        _pendingUpdate = null; RtUpdateBtn.Visibility = RtIgnoreBtn.Visibility = Visibility.Collapsed; RtStatus.Text = "Ignored.";
    }

    async void RtVerify_Click(object sender, RoutedEventArgs e)
    {
        var r = await _c.Runtime.VerifyAsync();
        RtStatus.Text = (r.Ok ? "✓ " : "✗ ") + r.Message + (r.ReportedVersion is null ? "" : "  " + r.ReportedVersion);
    }

    async void RtRepair_Click(object sender, RoutedEventArgs e) => await RtOp("Repairing runtime", p => _c.Runtime.RepairAsync(p));

    void RtRollback_Click(object sender, RoutedEventArgs e)
    {
        if (_c.Server.State == ServerState.Running) { RtStatus.Text = "Stop the server first."; return; }
        try { _c.Runtime.Rollback(); RtStatus.Text = "Rolled back."; _ = _c.ProbeDeviceAsync(); }
        catch (Exception ex) { RtStatus.Text = ex.Message; }
    }

    void RtRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_c.Server.State == ServerState.Running) { RtStatus.Text = "Stop the server first."; return; }
        if (MessageBox.Show("Remove the inference runtime? You can reinstall it from Home.", "Remove runtime", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _c.Runtime.Remove(); RtStatus.Text = "Removed.";
    }

    // ---- diagnostics ----

    void LoadLog()
    {
        var name = ((ComboBoxItem)LogBox.SelectedItem).Content as string;
        var log = name switch { "runtime" => _c.Logs.Runtime, "inference" => _c.Logs.Inference, "api" => _c.Logs.Api, _ => _c.Logs.App };
        LogView.Text = string.Join("\n", log.Tail(300));
        LogView.ScrollToEnd();
    }

    void LogBox_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded || LogView is not null) LoadLog(); }
    void LogRefresh_Click(object sender, RoutedEventArgs e) => LoadLog();
    void OpenLogs_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(_c.Paths.Logs) { UseShellExecute = true });
    void OpenData_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(_c.Paths.Root) { UseShellExecute = true });
}

/// <summary>Tiny modal text prompt.</summary>
public static class InputDialog
{
    public static string? Ask(Window owner, string title, string prompt, string? initial = null)
    {
        var box = new TextBox { Text = initial ?? "", Margin = new Thickness(0, 10, 0, 14) };
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(24, 6, 24, 6) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(cancel); row.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(box); panel.Children.Add(row);
        var win = new Window
        {
            Title = title, Content = panel, Owner = owner, Width = 560, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("Bg")
        };
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? box.Text.Trim() : null;
    }
}

/// <summary>Pick a curated model or enter a custom URL / owner/repo/file.gguf.</summary>
public static class DownloadDialog
{
    public static string? Ask(Window owner)
    {
        var combo = new ComboBox { Margin = new Thickness(0, 8, 0, 12) };
        foreach (var e in ModelCatalog.Entries)
            combo.Items.Add(new ComboBoxItem { Content = $"{e.Name}   ~{e.SizeGb:0.0} GB   {e.Note}", Tag = e.Source });
        combo.SelectedIndex = 0;
        var box = new TextBox { Margin = new Thickness(0, 8, 0, 14) };
        var ok = new Button { Content = "Download", IsDefault = true, Padding = new Thickness(24, 6, 24, 6) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(cancel); row.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Recommended for 16 GB cards:" });
        panel.Children.Add(combo);
        panel.Children.Add(new TextBlock { Text = "Or a custom .gguf URL / owner/repo/file.gguf (overrides the choice above):" });
        panel.Children.Add(box); panel.Children.Add(row);
        var win = new Window
        {
            Title = "Download model", Content = panel, Owner = owner, Width = 620, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("Bg")
        };
        ok.Click += (_, _) => win.DialogResult = true;
        if (win.ShowDialog() != true) return null;
        return string.IsNullOrWhiteSpace(box.Text) ? (string)((ComboBoxItem)combo.SelectedItem).Tag : box.Text.Trim();
    }
}
