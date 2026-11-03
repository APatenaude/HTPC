using System.Collections.Concurrent;
using System.Net;

namespace Htpc.Launcher;

// Checks for the speed test (SpeedTest.cs, Settings > Wi-Fi > Test speed): the numbers (Mb/s, the
// median, the request sizes, the log line, the meter), then a whole run against a fake server, so
// nothing leaves the machine.
static class SpeedTests
{
    static readonly Action<bool, string> Check = T.Check;

    // A short test: the real one runs 8 delay requests and two 5 s transfers.
    static readonly SpeedOptions Quick = new() { Warmup = TimeSpan.FromMilliseconds(200), Window = TimeSpan.FromMilliseconds(600), Sample = TimeSpan.FromMilliseconds(50) };

    public static void Run()
    {
        T.Group("Speed test: the numbers", Numbers);
        T.Group("Speed test: the meter", Meter);
        T.GroupAsync("Speed test: a whole run over a fake server", WholeRun);
        T.GroupAsync("Speed test: failures and cancelling", Failures);
    }

    static void Numbers()
    {
        Check(Math.Abs(SpeedRules.Mbps(12_500_000, TimeSpan.FromSeconds(1)) - 100) < 0.001, "12.5 MB in a second is 100 Mb/s");
        Check(Math.Abs(SpeedRules.Mbps(1_000_000, TimeSpan.FromMilliseconds(500)) - 16) < 0.001, "1 MB in half a second is 16 Mb/s");
        Check(SpeedRules.Mbps(5000, TimeSpan.Zero) == 0, "no time, no rate (not infinity)");

        Check(SpeedRules.Median([]) == 0, "median of nothing: 0");
        Check(SpeedRules.Median([30, 10, 20]) == 20, "median of three: the middle one");
        Check(SpeedRules.Median([40, 10, 30, 20]) == 25, "median of four: the mean of the middle two");
        Check(SpeedRules.Median([300, 11, 12, 13, 12, 11, 12, 14]) == 12, "median of 8 delays: the first request's connection time does not count");

        Check(SpeedRules.RequestBytes(true, 0) == 1_000_000 && SpeedRules.RequestBytes(true, 1) == 10_000_000 && SpeedRules.RequestBytes(true, 2) == 25_000_000, "download requests step up: 1 MB, 10 MB, 25 MB");
        Check(SpeedRules.RequestBytes(true, 50) == 25_000_000 && SpeedRules.RequestBytes(false, 50) == 25_000_000, "... then stay at 25 MB");
        Check(SpeedRules.RequestBytes(false, 0) == 1_000_000 && SpeedRules.RequestBytes(false, -3) == 1_000_000, "upload starts at 1 MB");
        Check(Enumerable.Range(0, 20).All(n => SpeedRules.RequestBytes(true, n) < 100_000_000), "no request reaches the 100 MB that Cloudflare refuses");

        Check(SpeedRules.Rate(312.4) == "312" && SpeedRules.Rate(41) == "41" && SpeedRules.Rate(10) == "10", "10 Mb/s and over: whole numbers");
        Check(SpeedRules.Rate(4.66) == "4.7" && SpeedRules.Rate(0) == "0.0", "under 10: one decimal");
        Check(SpeedRules.Rate(9.96) == "10", "9.96 is 10, not 10.0");

        var result = new SpeedResult(312.4, 41.2, 12, TimeSpan.FromSeconds(11.2));
        Check(SpeedRules.Summary(result, "Wi-Fi") == "Speed test: 312 Mb/s down, 41 Mb/s up, 12 ms, over Wi-Fi (11 s)", "the log line");
        Check(SpeedRules.Summary(result, null) == "Speed test: 312 Mb/s down, 41 Mb/s up, 12 ms (11 s)", "... without the connection when it is not known");

        Check(SpeedRules.ErrorText(new SpeedTestException("No internet")) == "No internet", "an error meant for the TV is shown as it is");
        Check(SpeedRules.ErrorText(new HttpRequestException("A connection could not be made.")).StartsWith("Could not reach the speed test server (A connection could not be made)"), "a network error: what failed, in the TV's words");
        Check(SpeedRules.ErrorText(new SpeedTestException(new string('x', 500))).Length <= 160, "a long error is cut to fit a row");
        Check(SpeedRules.LimitedText(TimeSpan.FromSeconds(3490)) == "Cloudflare is limiting speed tests from this network. Try again in about 59 minutes.", "limited for 3490 s: about 59 minutes (rounded up)");
        Check(SpeedRules.LimitedText(TimeSpan.FromSeconds(30)).EndsWith("in a minute.") && SpeedRules.LimitedText(null).EndsWith("later."), "limited for less than a minute, or for an unknown time");
    }

    static void Meter()
    {
        // 100 Mb/s (12.5 MB/s) from the end of the first second; slow before it (TCP ramping up).
        var meter = new SpeedMeter(TimeSpan.FromSeconds(1));
        Check(meter.Live == 0 && meter.Final == 0, "no sample yet: 0");
        for (var ms = 250; ms <= 5000; ms += 250)
            meter.Add(TimeSpan.FromMilliseconds(ms), ms <= 1000 ? 1000L * ms : 1_000_000 + 12_500L * (ms - 1000));
        Check(Math.Abs(meter.Final - 100) < 0.001, "the final rate leaves the first second out: 100 Mb/s");
        Check(Math.Abs(meter.Live - 100) < 0.001, "the live rate is the last second's: 100 Mb/s");

        var early = new SpeedMeter(TimeSpan.FromSeconds(1));
        early.Add(TimeSpan.FromMilliseconds(500), 1_000_000);
        Check(Math.Abs(early.Final - 16) < 0.001, "a transfer that ended inside the warm-up still has a rate, from its start");
    }

    static async Task WholeRun()
    {
        var server = new FakeServer();
        var updates = new ConcurrentQueue<SpeedUpdate>();
        using var test = new SpeedTest(server, Quick);
        var result = await test.Run(updates.Enqueue, CancellationToken.None);
        var all = updates.ToArray();

        Check(server.DelayCalls == 8, $"the delay is 8 requests (made {server.DelayCalls})");
        Check(result.DelayMs >= 0 && result.DownMbps > 0 && result.UpMbps > 0, $"all three figures came out ({result.DelayMs} ms, {result.DownMbps:0.0} down, {result.UpMbps:0.0} up)");
        Check(all.Select(u => u.Phase).Distinct().SequenceEqual(["delay", "down", "up"]), "the phases come in order: delay, down, up");
        Check(all.Count(u => u.Phase == "down") >= 2 && all.Count(u => u.Phase == "up") >= 2, "each transfer reports as it goes");
        Check(all.Where(u => u.Phase != "delay").All(u => u.DelayMs == result.DelayMs) && all.Where(u => u.Phase == "up").All(u => u.DownMbps == result.DownMbps), "later phases carry the earlier results");
        Check(server.DownSizes.Count(s => s == 1_000_000) >= 4, "four download streams start, each with a 1 MB request");
        Check(server.UploadLengths.Count(s => s == 1_000_000) >= 4, "four upload streams start, each with a 1 MB body");
        Check(server.Foreign == 0, "every request goes to https://speed.cloudflare.com, over HTTP/1.1");
    }

    static async Task Failures()
    {
        foreach (var (mode, expected) in new[] { (FakeMode.Forbidden, "403"), (FakeMode.NoDownload, "403"), (FakeMode.Limited, "limiting speed tests"),
            (FakeMode.LimitedAfterFirst, "limiting speed tests"),
            (FakeMode.Offline, "Could not reach the speed test server (No such host is known)") })
        {
            using var test = new SpeedTest(new FakeServer { Mode = mode }, Quick);
            try
            {
                await test.Run(_ => { }, CancellationToken.None);
                Check(false, $"{mode}: an error");
            }
            catch (SpeedTestException e) { Check(e.Message.Contains(expected), $"{mode}: the error says what happened (\"{e.Message}\")"); }
        }

        using var cancelled = new CancellationTokenSource();
        using var running = new SpeedTest(new FakeServer(), Quick);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await running.Run(u => { if (u.Phase == "down" && u.DownMbps > 0) cancelled.Cancel(); }, cancelled.Token);
            Check(false, "cancelled in the download: stops");
        }
        catch (OperationCanceledException) { Check(clock.Elapsed < TimeSpan.FromSeconds(3), "cancelled in the download: stops at once, with a cancellation and not an error"); }
    }

    enum FakeMode { Ok, Forbidden, NoDownload, Limited, LimitedAfterFirst, Offline }

    // Cloudflare's two endpoints, in memory: __down?bytes=N answers N zero bytes (N = 0: the delay probe), __up takes a body.
    sealed class FakeServer : HttpMessageHandler
    {
        public FakeMode Mode;
        public int DelayCalls, Foreign;
        public readonly ConcurrentBag<long> DownSizes = [], UploadLengths = [];

        static HttpResponseMessage TooMany()
        {
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new(TimeSpan.FromMinutes(10));
            return limited;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var url = request.RequestUri!;
            if (url.Scheme != "https" || url.Host != "speed.cloudflare.com" || request.Version != HttpVersion.Version11) Interlocked.Increment(ref Foreign);
            if (Mode == FakeMode.Offline) throw new HttpRequestException("No such host is known.");
            if (Mode == FakeMode.Forbidden) return new(HttpStatusCode.Forbidden);
            if (Mode == FakeMode.Limited) return TooMany();
            if (url.AbsolutePath == "/__down")
            {
                var bytes = long.Parse(url.Query.Split('=')[1]);
                if (bytes == 0) { Interlocked.Increment(ref DelayCalls); return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }; }
                DownSizes.Add(bytes);
                if (Mode == FakeMode.LimitedAfterFirst && bytes > 1_000_000) return TooMany();
                return Mode == FakeMode.NoDownload ? new(HttpStatusCode.Forbidden) : new(HttpStatusCode.OK) { Content = new StreamContent(new Body(bytes)) };
            }
            if (url.AbsolutePath == "/__up")
            {
                UploadLengths.Add(request.Content!.Headers.ContentLength ?? -1);
                await request.Content.CopyToAsync(new Sink(), cancel);
                return new(HttpStatusCode.OK);
            }
            return new(HttpStatusCode.NotFound);
        }
    }

    // A readable stream of zeros, 64 KB a millisecond at most: a link, not a loop that starves the sampler.
    sealed class Body(long length) : Stream
    {
        long left = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            if (left == 0) return 0;
            await Task.Delay(1, cancel);
            var n = (int)Math.Min(Math.Min(left, buffer.Length), 65536);
            buffer.Span[..n].Clear();
            left -= n;
            return n;
        }
    }

    // A writable stream that throws its bytes away, as slowly as the Body is read.
    sealed class Sink : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancel) => WriteAsync(buffer.AsMemory(offset, count), cancel).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancel = default) => await Task.Delay(1, cancel);
    }
}
