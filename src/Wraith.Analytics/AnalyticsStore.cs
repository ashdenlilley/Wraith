using Microsoft.Data.Sqlite;
using Wraith.Core;

namespace Wraith.Analytics;

/// <summary>SQLite store. Metadata only: prompts and responses are never persisted.</summary>
public sealed class AnalyticsStore : IDisposable
{
    readonly SqliteConnection _db;
    readonly object _lock = new();

    public AnalyticsStore(AppPaths paths) : this(paths.Database) { }

    public AnalyticsStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS sessions(
              id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp TEXT, model TEXT, quantization TEXT, context_size INTEGER,
              gpu TEXT, runtime_version TEXT, duration REAL, prompt_tokens INTEGER, generated_tokens INTEGER,
              prompt_tps REAL, generation_tps REAL, ttft REAL, peak_vram INTEGER, average_power REAL,
              average_gpu_utilisation REAL, peak_tps REAL, min_tps REAL);
            CREATE TABLE IF NOT EXISTS requests(
              id INTEGER PRIMARY KEY AUTOINCREMENT, session_id INTEGER, timestamp TEXT, model TEXT, endpoint TEXT,
              prompt_tokens INTEGER, completion_tokens INTEGER, ttft REAL, prompt_tps REAL, generation_tps REAL,
              duration REAL, status INTEGER);
            CREATE TABLE IF NOT EXISTS benchmarks(
              id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp TEXT, model TEXT, quantization TEXT, context_size INTEGER,
              gpu TEXT, runtime_version TEXT, iterations INTEGER, prompt_tokens INTEGER, generated_tokens INTEGER,
              gen_avg REAL, gen_median REAL, gen_min REAL, gen_max REAL, prompt_tps REAL, ttft REAL,
              peak_vram INTEGER, avg_power REAL, peak_temp REAL, peak_hotspot REAL, avg_clock REAL, avg_mem_clock REAL,
              perf_per_watt REAL, label TEXT);
            CREATE TABLE IF NOT EXISTS stability_tests(
              id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp TEXT, model TEXT, duration REAL, tokens INTEGER,
              avg_tps REAL, min_tps REAL, errors INTEGER, crashes INTEGER, gpu_errors INTEGER,
              peak_vram INTEGER, avg_power REAL, peak_temp REAL, passed INTEGER);
            """);
    }

    // ---------- sessions ----------

    public long InsertSession(string model, string? quant, int ctx, string gpu, string? runtime) =>
        Insert("INSERT INTO sessions(timestamp,model,quantization,context_size,gpu,runtime_version) VALUES($t,$m,$q,$c,$g,$r)",
            ("$t", Now()), ("$m", model), ("$q", quant), ("$c", ctx), ("$g", gpu), ("$r", runtime));

    public void UpdateSession(long id, SessionStats s, TelemetrySummary hw) =>
        Run("""
            UPDATE sessions SET duration=$d, prompt_tokens=$pt, generated_tokens=$gt, prompt_tps=$ptps, generation_tps=$gtps,
              ttft=$ttft, peak_vram=$vram, average_power=$pw, average_gpu_utilisation=$u, peak_tps=$pk, min_tps=$mn WHERE id=$id
            """,
            ("$d", s.Duration.TotalSeconds), ("$pt", s.PromptTokens), ("$gt", s.GeneratedTokens), ("$ptps", s.PromptTps),
            ("$gtps", s.GenerationTps), ("$ttft", s.MedianTtft), ("$vram", hw.PeakVramBytes), ("$pw", hw.AvgPowerW),
            ("$u", hw.AvgGpuUtil), ("$pk", s.PeakTps), ("$mn", s.MinTps), ("$id", id));

    public void InsertRequest(long sessionId, RequestRecord r) =>
        Run("INSERT INTO requests(session_id,timestamp,model,endpoint,prompt_tokens,completion_tokens,ttft,prompt_tps,generation_tps,duration,status) VALUES($s,$t,$m,$e,$p,$c,$ttft,$ptps,$gtps,$d,$st)",
            ("$s", sessionId), ("$t", r.Timestamp.ToString("o")), ("$m", r.Model), ("$e", r.Endpoint), ("$p", r.PromptTokens),
            ("$c", r.CompletionTokens), ("$ttft", r.TtftSeconds), ("$ptps", r.PromptTps), ("$gtps", r.GenerationTps),
            ("$d", r.DurationSeconds), ("$st", r.Status));

    public IReadOnlyList<SessionRow> RecentSessions(int n = 20) =>
        Query("SELECT id,timestamp,model,quantization,context_size,duration,prompt_tokens,generated_tokens,prompt_tps,generation_tps,ttft,peak_vram,average_power FROM sessions ORDER BY id DESC LIMIT $n",
            r => new SessionRow(r.GetInt64(0), r.GetString(1), r.GetString(2), Str(r, 3), Int(r, 4), Dbl(r, 5) ?? 0, Int(r, 6) ?? 0,
                Int(r, 7) ?? 0, Dbl(r, 8), Dbl(r, 9), Dbl(r, 10), r.IsDBNull(11) ? null : r.GetInt64(11), Dbl(r, 12)), ("$n", n));

    public ApiTotals ApiTotalsAllTime()
    {
        var rows = Query("SELECT COUNT(*),COALESCE(SUM(prompt_tokens),0),COALESCE(SUM(completion_tokens),0),AVG(prompt_tps),AVG(generation_tps) FROM requests WHERE status BETWEEN 200 AND 299",
            r => new ApiTotals(r.GetInt32(0), r.GetInt64(1), r.GetInt64(2), Dbl(r, 3), Dbl(r, 4), null));
        var ttfts = Query("SELECT ttft FROM requests WHERE ttft IS NOT NULL ORDER BY ttft", r => r.GetDouble(0));
        return rows[0] with { MedianTtft = Stats.Median(ttfts) };
    }

    // ---------- benchmarks ----------

    public long InsertBenchmark(BenchmarkResult b) =>
        Insert("""
            INSERT INTO benchmarks(timestamp,model,quantization,context_size,gpu,runtime_version,iterations,prompt_tokens,generated_tokens,
              gen_avg,gen_median,gen_min,gen_max,prompt_tps,ttft,peak_vram,avg_power,peak_temp,peak_hotspot,avg_clock,avg_mem_clock,perf_per_watt,label)
            VALUES($t,$m,$q,$c,$g,$r,$i,$pt,$gt,$ga,$gm,$gn,$gx,$pp,$tt,$v,$pw,$pk,$ph,$ac,$am,$pf,$l)
            """,
            ("$t", b.Timestamp.ToString("o")), ("$m", b.Model), ("$q", b.Quantization), ("$c", b.ContextSize), ("$g", b.Gpu),
            ("$r", b.RuntimeVersion), ("$i", b.Iterations), ("$pt", b.PromptTokens), ("$gt", b.GeneratedTokens),
            ("$ga", b.GenAvg), ("$gm", b.GenMedian), ("$gn", b.GenMin), ("$gx", b.GenMax), ("$pp", b.PromptTps), ("$tt", b.Ttft),
            ("$v", b.Hardware.PeakVramBytes), ("$pw", b.Hardware.AvgPowerW), ("$pk", b.Hardware.PeakTempC),
            ("$ph", b.Hardware.PeakHotspotC), ("$ac", b.Hardware.AvgGpuClockMhz), ("$am", b.Hardware.AvgMemClockMhz),
            ("$pf", b.PerfPerWatt), ("$l", b.Label));

    public IReadOnlyList<BenchmarkResult> Benchmarks(string? model = null, int n = 50) =>
        Query("""
            SELECT id,timestamp,model,quantization,context_size,gpu,runtime_version,iterations,prompt_tokens,generated_tokens,
              gen_avg,gen_median,gen_min,gen_max,prompt_tps,ttft,peak_vram,avg_power,peak_temp,peak_hotspot,avg_clock,avg_mem_clock,perf_per_watt,label
            FROM benchmarks WHERE ($m IS NULL OR model=$m) ORDER BY id DESC LIMIT $n
            """,
            r => new BenchmarkResult(r.GetInt64(0), DateTime.Parse(r.GetString(1)).ToUniversalTime(), r.GetString(2), Str(r, 3), Int(r, 4) ?? 0,
                Str(r, 5) ?? "", Str(r, 6), Int(r, 7) ?? 0, Int(r, 8) ?? 0, Int(r, 9) ?? 0,
                Dbl(r, 10) ?? 0, Dbl(r, 11) ?? 0, Dbl(r, 12) ?? 0, Dbl(r, 13) ?? 0, Dbl(r, 14) ?? 0, Dbl(r, 15) ?? 0,
                new TelemetrySummary(0, null, r.IsDBNull(16) ? null : r.GetInt64(16), Dbl(r, 17), Dbl(r, 18), Dbl(r, 19), Dbl(r, 20), Dbl(r, 21)),
                Dbl(r, 22), Str(r, 23)), ("$m", model), ("$n", n));

    public long InsertStability(StabilityResult s) =>
        Insert("INSERT INTO stability_tests(timestamp,model,duration,tokens,avg_tps,min_tps,errors,crashes,gpu_errors,peak_vram,avg_power,peak_temp,passed) VALUES($t,$m,$d,$tk,$a,$mn,$e,$c,$g,$v,$p,$pt,$ok)",
            ("$t", s.Timestamp.ToString("o")), ("$m", s.Model), ("$d", s.Duration.TotalSeconds), ("$tk", s.Tokens), ("$a", s.AvgTps),
            ("$mn", s.MinTps), ("$e", s.Errors), ("$c", s.Crashes), ("$g", s.GpuErrors), ("$v", s.Hardware.PeakVramBytes),
            ("$p", s.Hardware.AvgPowerW), ("$pt", s.Hardware.PeakTempC), ("$ok", s.Passed ? 1 : 0));

    // ---------- plumbing ----------

    static string Now() => DateTime.UtcNow.ToString("o");
    static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    static int? Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    static double? Dbl(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    void Exec(string sql) { lock (_lock) { using var c = _db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); } }

    static void Bind(SqliteCommand c, (string, object?)[] ps)
    {
        foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v ?? DBNull.Value);
    }

    void Run(string sql, params (string, object?)[] ps)
    {
        lock (_lock) { using var c = _db.CreateCommand(); c.CommandText = sql; Bind(c, ps); c.ExecuteNonQuery(); }
    }

    long Insert(string sql, params (string, object?)[] ps)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand(); c.CommandText = sql + "; SELECT last_insert_rowid();"; Bind(c, ps);
            return (long)c.ExecuteScalar()!;
        }
    }

    List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] ps)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand(); c.CommandText = sql; Bind(c, ps);
            using var r = c.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    public void Dispose() => _db.Dispose();
}

public sealed record SessionRow(long Id, string Timestamp, string Model, string? Quantization, int? ContextSize, double DurationSeconds,
    int PromptTokens, int GeneratedTokens, double? PromptTps, double? GenerationTps, double? Ttft, long? PeakVram, double? AvgPower);

public sealed record ApiTotals(int Requests, long InputTokens, long OutputTokens, double? PromptTps, double? GenerationTps, double? MedianTtft);
