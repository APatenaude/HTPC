using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace Htpc.Launcher;

/// <summary>One step of a running speed test. Phase: delay, down or up; a rate is null until the phase has one.</summary>
sealed record SpeedUpdate(string Phase, double? DownMbps, double? UpMbps, int? DelayMs);

sealed record SpeedResult(double DownMbps, double UpMbps, int DelayMs, TimeSpan Took);

/// <summary>A failure whose message is meant for the TV (Settings › Wi-Fi shows it as it is).</summary>
sealed class SpeedTestException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>How long and how wide a test goes. The defaults are the product's; the checks shorten the times.</summary>
sealed record SpeedOptions
{
    public int DelayRequests { get; init; } = 8;
    public int Streams { get; init; } = 4;
    /// <summary>Counted out of each transfer's rate: TCP is still ramping up in the first second.</summary>
    public TimeSpan Warmup { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>How long each of download and upload runs, the warm-up included.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan Sample { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>A request that gets no answer for this long ends the delay measurement.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(8);
}

/// <summary>
/// The numbers of the speed test that need no network (SpeedTest uses them; launcher\tests\LauncherTests
/// checks them): Mb/s, the median delay, how big each request is, the log line.
/// </summary>
static class SpeedRules
{
    public const string Server = "https://speed.cloudflare.com";

    // Cloudflare refuses a __down of 100 MB or more (403); each request stays well under, and a fast
    // link just asks again. Sizes step up so a slow link gets an answer early and a fast one a long run.
    static readonly long[] DownloadSizes = [1_000_000, 10_000_000, 25_000_000];
    static readonly long[] UploadSizes = [1_000_000, 5_000_000, 10_000_000, 25_000_000];

    /// <summary>Megabits per second (10^6 bits) for this many bytes over this time; 0 for no time.</summary>
    public static double Mbps(long bytes, TimeSpan elapsed) => elapsed <= TimeSpan.Zero ? 0 : bytes * 8 / elapsed.TotalSeconds / 1_000_000;

    /// <summary>The middle value (the mean of the two middle ones for an even count); 0 for none.</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>The size of one stream's n-th request (counting from 0): the plan's steps, then its last size again.</summary>
    public static long RequestBytes(bool download, int n)
    {
        var plan = download ? DownloadSizes : UploadSizes;
        return plan[Math.Min(Math.Max(n, 0), plan.Length - 1)];
    }

    /// <summary>"312" for 312.4, "4.7" for 4.66: a decimal only under 10, where it matters.</summary>
    public static string Rate(double mbps) => Math.Round(mbps, 1) >= 10 ? Math.Round(mbps).ToString("0", CultureInfo.InvariantCulture) : mbps.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>The launcher.log line of a finished run (via: "Wi-Fi", "Ethernet" or null).</summary>
    public static string Summary(SpeedResult r, string? via) =>
        $"Speed test: {Rate(r.DownMbps)} Mb/s down, {Rate(r.UpMbps)} Mb/s up, {r.DelayMs} ms{(via is null ? "" : ", over " + via)} ({Math.Round(r.Took.TotalSeconds):0} s)";

    /// <summary>Cloudflare answers 429 with a Retry-After (about an hour) once a network has used its share of the free test.</summary>
    public static string LimitedText(TimeSpan? wait)
    {
        var minutes = wait is { } w ? (int)Math.Ceiling(w.TotalMinutes) : 0;
        return "Cloudflare is limiting speed tests from this network. Try again " + minutes switch
        {
            <= 0 => "later.",
            1 => "in a minute.",
            _ => $"in about {minutes} minutes.",
        };
    }

    /// <summary>What the TV says when a test failed: short, and never longer than a row can hold.</summary>
    public static string ErrorText(Exception e)
    {
        var text = e is SpeedTestException ? e.Message : e is TimeoutException or OperationCanceledException ? "The speed test server stopped answering"
            : $"Could not reach the speed test server ({(e.InnerException ?? e).Message.Trim().TrimEnd('.')})";
        return text.Length > 160 ? text[..159] + "…" : text;
    }
}

/// <summary>
/// Bytes counted as a transfer runs, read as Mb/s: Live over the last second, Final from the end of
/// the warm-up to the last sample. Samples come in time order.
/// </summary>
sealed class SpeedMeter(TimeSpan warmup)
{
    readonly List<(TimeSpan At, long Bytes)> samples = [(TimeSpan.Zero, 0)];

    public void Add(TimeSpan at, long totalBytes) => samples.Add((at, totalBytes));

    public double Live => Between(samples[^1].At - TimeSpan.FromSeconds(1));

    // A transfer that ended inside the warm-up is read from its start: some rate, not none.
    public double Final => Between(samples[^1].At > warmup ? warmup : TimeSpan.Zero);

    // Mb/s from the last sample at or before `from` (the first one if none) to the last sample.
    double Between(TimeSpan from)
    {
        var last = samples[^1];
        var start = samples.LastOrDefault(s => s.At <= from, samples[0]);
        return SpeedRules.Mbps(last.Bytes - start.Bytes, last.At - start.At);
    }
}

/// <summary>
/// Download, upload and delay against Cloudflare's public speed test, over the handler it is given
/// (SpeedTest.CreateHandler on the box; a fake one in the checks). Download and upload each run
/// several streams for a few seconds; a stream asks again when its request ends. Nothing is
/// stored and nothing is sent but random bytes.
/// </summary>
sealed class SpeedTest : IDisposable
{
    readonly HttpClient http;
    readonly SpeedOptions options;

    public SpeedTest(HttpMessageHandler handler, SpeedOptions? options = null)
    {
        this.options = options ?? new SpeedOptions();
        // No timeout of its own: a test is cut short by its token. HTTP/1.1 gives each stream a
        // connection of its own (HTTP/2 would carry all four over one).
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, DefaultRequestVersion = HttpVersion.Version11 };
    }

    /// <summary>Straight to the internet only (AppLogos.ConnectToInternet: nothing reaches the LAN), no proxy, cookies, redirects or compression.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = AppLogos.ConnectToInternet,
    };

    /// <summary>
    /// Delay, then download, then upload; progress is called with each sample (about four a second).
    /// Throws OperationCanceledException when `cancel` is cancelled, SpeedTestException for any failure.
    /// </summary>
    public async Task<SpeedResult> Run(Action<SpeedUpdate> progress, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            progress(new("delay", null, null, null));
            var delay = await MeasureDelay(cancel);
            progress(new("down", 0, null, delay));
            var down = await Transfer(true, mbps => progress(new("down", mbps, null, delay)), cancel);
            progress(new("up", down, 0, delay));
            var up = await Transfer(false, mbps => progress(new("up", down, mbps, delay)), cancel);
            return new SpeedResult(down, up, delay, clock.Elapsed);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw new SpeedTestException(SpeedRules.ErrorText(e), e);
        }
        catch (OperationCanceledException e) when (!cancel.IsCancellationRequested)
        {
            throw new SpeedTestException(SpeedRules.ErrorText(e), e);   // a request's own time-out
        }
    }

    async Task<int> MeasureDelay(CancellationToken cancel)
    {
        var times = new List<double>();
        for (var i = 0; i < options.DelayRequests; i++)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(options.RequestTimeout);
            var start = Stopwatch.GetTimestamp();
            using var response = await http.GetAsync($"{SpeedRules.Server}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, limit.Token);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            EnsureOk(response);
        }
        // The first request pays for the connection (DNS, TLS); the median leaves it out.
        return (int)Math.Round(SpeedRules.Median(times));
    }

    async Task<double> Transfer(bool download, Action<double> live, CancellationToken cancel)
    {
        long total = 0;
        Exception? failure = null;
        void Count(int n) => Interlocked.Add(ref total, n);
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var workers = Task.WhenAll(Enumerable.Range(0, options.Streams).Select(_ => Task.Run(async () =>
        {
            try
            {
                for (var n = 0; ; n++)
                {
                    var bytes = SpeedRules.RequestBytes(download, n);
                    if (download) await DownloadOnce(bytes, Count, phase.Token);
                    else await UploadOnce(bytes, Count, phase.Token);
                }
            }
            catch (OperationCanceledException) { }   // the window ended, or the test was cancelled
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref failure, e, null);
                // The server refused (429 limiting, 403): the other streams' numbers mean nothing now.
                if (e is SpeedTestException) phase.Cancel();
            }
        })));

        var meter = new SpeedMeter(options.Warmup);
        var clock = Stopwatch.StartNew();
        bool allEnded;
        try
        {
            while (clock.Elapsed < options.Window && !workers.IsCompleted)
            {
                await Task.WhenAny(Task.Delay(options.Sample, cancel), workers);
                cancel.ThrowIfCancellationRequested();
                meter.Add(clock.Elapsed, Interlocked.Read(ref total));
                live(meter.Live);
            }
            allEnded = workers.IsCompleted;
        }
        finally
        {
            phase.Cancel();
            // A stream stuck closing its connection must not hold the result up.
            await Task.WhenAny(workers, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        cancel.ThrowIfCancellationRequested();
        if (failure is SpeedTestException refused) throw refused;
        // Every stream failed (the link dropping): not a rate worth showing.
        if (failure is not null && (allEnded || Interlocked.Read(ref total) == 0))
            throw failure as SpeedTestException ?? new SpeedTestException(SpeedRules.ErrorText(failure), failure);
        if (Interlocked.Read(ref total) == 0) throw new SpeedTestException("No data came through");
        return meter.Final;
    }

    async Task DownloadOnce(long bytes, Action<int> count, CancellationToken cancel)
    {
        using var response = await http.GetAsync($"{SpeedRules.Server}/__down?bytes={bytes}", HttpCompletionOption.ResponseHeadersRead, cancel);
        EnsureOk(response);
        await using var body = await response.Content.ReadAsStreamAsync(cancel);
        var buffer = new byte[65536];
        int n;
        while ((n = await body.ReadAsync(buffer, cancel)) > 0) count(n);
    }

    async Task UploadOnce(long bytes, Action<int> count, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SpeedRules.Server}/__up") { Content = new RandomContent(bytes, count) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        EnsureOk(response);
    }

    static void EnsureOk(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = response.Headers.RetryAfter is { } after ? after.Delta ?? after.Date - DateTimeOffset.UtcNow : null;
            throw new SpeedTestException(SpeedRules.LimitedText(wait));
        }
        if (!response.IsSuccessStatusCode) throw new SpeedTestException($"The speed test server answered {(int)response.StatusCode}");
    }

    public void Dispose() => http.Dispose();

    /// <summary>A body of random bytes of a set length, counted as it is written to the connection.</summary>
    sealed class RandomContent : HttpContent
    {
        // One block of random bytes, sent over and over: the link does not compress and the server throws the body away.
        static readonly byte[] Block = System.Security.Cryptography.RandomNumberGenerator.GetBytes(65536);
        readonly long length;
        readonly Action<int> count;

        public RandomContent(long length, Action<int> count)
        {
            this.length = length;
            this.count = count;
            Headers.ContentType = new("application/octet-stream");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancel)
        {
            for (var left = length; left > 0;)
            {
                var n = (int)Math.Min(left, Block.Length);
                await stream.WriteAsync(Block.AsMemory(0, n), cancel);
                count(n);
                left -= n;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = this.length;
            return true;
        }
    }
}
