using System.Net;
using System.Text;

namespace Htpc.Launcher;

// The box's logs over the home network (PhoneLogs, PhoneServer.Logs).
static partial class Program
{
    static async Task LogTests()
    {
        Check(new[] { "127.0.0.1", "::1", "10.1.2.3", "172.16.0.5", "172.31.255.1", "192.168.1.20", "169.254.3.4", "fe80::1", "fd12:3456::1", "::ffff:192.168.1.20" }
            .All(a => PhoneLogs.IsLocalNetwork(IPAddress.Parse(a))), "loopback, private, link-local and unique-local addresses are the home network");
        Check(!new[] { "8.8.8.8", "172.32.0.1", "192.169.0.1", "2001:4860:4860::8888", "::ffff:8.8.8.8" }
            .Any(a => PhoneLogs.IsLocalNetwork(IPAddress.Parse(a))) && !PhoneLogs.IsLocalNetwork(null), "internet addresses are not");

        // A Moonlight log of its own in the temp folder: the newest of its kind, so it is listed.
        var name = $"Moonlight-htpc-test-{Guid.NewGuid():N}.log";
        var file = Path.Combine(Path.GetTempPath(), name);
        await File.WriteAllTextAsync(file, "one\ntwo\nthree\nExecuting request: https://10.0.0.2:47984/launch?uniqueid=0123&appid=1&rikey=ABCDEF&rikeyid=77&mode=1920x1080\n");
        try
        {
            await using var box = await TestServer.StartAsync();
            var http = box.Http;
            var index = await http.GetAsync("/logs");
            var listing = await index.Content.ReadAsStringAsync();
            Check(index.StatusCode == HttpStatusCode.OK && index.Content.Headers.ContentType?.MediaType == "text/plain" && listing.Contains($"moonlight/{name}"),
                "GET /logs lists the logs as plain text");
            var tail = await http.GetAsync($"/logs/moonlight/{name}?lines=2");
            var lastTwo = await tail.Content.ReadAsStringAsync();
            Check(tail.StatusCode == HttpStatusCode.OK && lastTwo.StartsWith("three\n"), "a log's last lines");
            var all = await http.GetStringAsync($"/logs/moonlight/{name}");
            Check(all.StartsWith("one\ntwo\nthree\n"), "all of a short log by default");
            Check(!all.Contains("ABCDEF") && !all.Contains("=77") && !all.Contains("=0123") && all.Contains("appid=1") && all.Contains("rikey=redacted"),
                "Moonlight's launch request: the input key and ids are redacted, the rest stays"
);
            Check((await http.GetAsync("/logs/moonlight/nothing.log")).StatusCode == HttpStatusCode.NotFound, "a log that is not listed: 404");
            CheckAll(new[] { "/logs/user/..%5C..%5CWindows%5Cwin.ini", "/logs/..%2Findex.html", "/logs/user/../../x", $"/logs/other/{name}", "/logs//" + name },
                p => http.GetAsync(p).Result.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
                "a path out of the listed logs: not served");
            Check((await http.PostAsync("/logs", new StringContent(""))).StatusCode == HttpStatusCode.MethodNotAllowed, "read-only: POST is refused");
            var wrongHost = new HttpRequestMessage(HttpMethod.Get, "/logs") { Headers = { Host = "evil.example" } };
            Check((await http.SendAsync(wrongHost)).StatusCode == HttpStatusCode.MisdirectedRequest, "the Host header must still name the box");
        }
        finally { File.Delete(file); }
    }
}
