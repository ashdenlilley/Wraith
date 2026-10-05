using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Wraith.Core;

namespace Wraith.Hardware;

/// <summary>Samples GPU/CPU/RAM telemetry at a fixed interval (default 1 s).</summary>
public sealed class HardwareMonitor : ITelemetryProvider, IDisposable
{
    readonly Computer _computer;
    readonly Timer _timer;
    readonly LogChannel _log;
    readonly object _lock = new();
    readonly List<Window> _windows = new();
    IHardware? _gpu;
    long _prevIdle, _prevTotal;
    int _busy;

    public TelemetrySample? Latest { get; private set; }
    public bool GpuSensorsAvailable => _gpu is not null;
    public event Action<TelemetrySample>? Sample;

    public HardwareMonitor(LogChannel log, string? preferredGpuName = null, TimeSpan? interval = null)
    {
        _log = log;
        _computer = new Computer { IsGpuEnabled = true };
        try
        {
            _computer.Open();
            _gpu = PickGpu(_computer.Hardware.Where(h =>
                h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel).ToList(), preferredGpuName);
            log.Info(_gpu is null ? "No GPU sensors found." : $"GPU sensors: {_gpu.Name}");
        }
        catch (Exception ex) { log.Error("Hardware sensor init failed", ex); }
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(500), interval ?? TimeSpan.FromSeconds(1));
    }

    static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).ToArray().Select(char.ToLowerInvariant).ToArray());

    /// <summary>Prefer the GPU the detector chose (by name); otherwise the one with the most dedicated memory.</summary>
    static IHardware? PickGpu(List<IHardware> gpus, string? preferred)
    {
        if (gpus.Count <= 1) return gpus.FirstOrDefault();
        if (!string.IsNullOrEmpty(preferred))
        {
            var n = Norm(preferred);
            var hit = gpus.FirstOrDefault(g => Norm(g.Name) == n) ?? gpus.FirstOrDefault(g => Norm(g.Name).Contains(n) || n.Contains(Norm(g.Name)));
            if (hit is not null) return hit;
        }
        double Mem(IHardware g)
        {
            g.Update();
            return g.Sensors.Where(x => x.SensorType == SensorType.SmallData && x.Name.Contains("Total") && x.Value.HasValue)
                .Select(x => (double)x.Value!.Value).DefaultIfEmpty(0).Max();
        }
        return gpus.OrderByDescending(Mem).First();
    }

    void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var s = Read();
            Latest = s;
            lock (_lock) foreach (var w in _windows) w.Add(s);
            Sample?.Invoke(s);
        }
        catch (Exception ex) { _log.Error("Telemetry sample failed", ex); }
        finally { _busy = 0; }
    }

    TelemetrySample Read()
    {
        double? util = null, temp = null, hot = null, clk = null, mclk = null, pwr = null, fan = null;
        long? vram = null;
        if (_gpu is not null)
        {
            _gpu.Update();
            foreach (var s in _gpu.Sensors)
            {
                if (s.Value is not float v) continue;
                var n = s.Name;
                switch (s.SensorType)
                {
                    case SensorType.Load when n == "GPU Core": util = v; break;
                    case SensorType.Load when util is null && n.Contains("Core"): util = v; break;
                    case SensorType.SmallData when n == "GPU Memory Used": vram = (long)(v * 1048576L); break;
                    case SensorType.Temperature when n is "GPU Hot Spot" or "GPU Hotspot": hot = v; break;
                    case SensorType.Temperature when n is "GPU Core" or "GPU Temperature": temp = v; break;
                    case SensorType.Clock when n == "GPU Core": clk = v; break;
                    case SensorType.Clock when n == "GPU Memory": mclk = v; break;
                    case SensorType.Power when n is "GPU Package" or "GPU Core" or "GPU Total Board Power": pwr = (pwr is null || n == "GPU Package") ? v : pwr; break;
                    case SensorType.Fan: fan = v; break;
                }
            }
        }
        var (_, ramUsed) = HardwareDetector.Ram();
        return new TelemetrySample(DateTime.UtcNow, util, vram, temp, hot, clk, mclk, pwr, fan, CpuUtil(), ramUsed);
    }

    [DllImport("kernel32.dll")]
    static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    double? CpuUtil()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var total = kernel + user;
        var di = idle - _prevIdle; var dt = total - _prevTotal;
        _prevIdle = idle; _prevTotal = total;
        return dt <= 0 ? null : Math.Clamp((1.0 - (double)di / dt) * 100, 0, 100);
    }

    public ITelemetryWindow BeginWindow()
    {
        var w = new Window(this);
        lock (_lock) _windows.Add(w);
        return w;
    }

    void Remove(Window w) { lock (_lock) _windows.Remove(w); }

    public void Dispose()
    {
        _timer.Dispose();
        try { _computer.Close(); } catch { }
    }

    sealed class Window : ITelemetryWindow
    {
        readonly HardwareMonitor _owner;
        readonly object _l = new();
        int _n, _nUtil, _nPwr, _nClk, _nMclk;
        double _util, _pwr, _clk, _mclk;
        long? _peakVram;
        double? _peakTemp, _peakHot;

        public Window(HardwareMonitor o) => _owner = o;

        public void Add(TelemetrySample s)
        {
            lock (_l)
            {
                _n++;
                if (s.GpuUtil is { } u) { _util += u; _nUtil++; }
                if (s.PowerW is { } p) { _pwr += p; _nPwr++; }
                if (s.GpuClockMhz is { } c) { _clk += c; _nClk++; }
                if (s.MemClockMhz is { } m) { _mclk += m; _nMclk++; }
                if (s.VramUsedBytes is { } v) _peakVram = Math.Max(_peakVram ?? 0, v);
                if (s.TempC is { } t) _peakTemp = Math.Max(_peakTemp ?? t, t);
                if (s.HotspotC is { } h) _peakHot = Math.Max(_peakHot ?? h, h);
            }
        }

        public TelemetrySummary Summarize()
        {
            lock (_l)
                return new(_n, _nUtil > 0 ? _util / _nUtil : null, _peakVram, _nPwr > 0 ? _pwr / _nPwr : null,
                    _peakTemp, _peakHot, _nClk > 0 ? _clk / _nClk : null, _nMclk > 0 ? _mclk / _nMclk : null);
        }

        public void Dispose() => _owner.Remove(this);
    }
}
