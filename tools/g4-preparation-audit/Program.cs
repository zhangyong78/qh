using Qh.Core;
using System.Diagnostics;
using System.Text.Json;

if (args.Length < 2) return 2;
var stages = new List<string>();
var gateway = new TqSdkGateway(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
var started = Stopwatch.GetTimestamp();
var slow = await TqSdkConnectionWorkflow.ProbeAsync(gateway, new TqSdkCredentials("not-used", "not-used"), CancellationToken.None, stages.Add);
var slowElapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
using var cancelled = new CancellationTokenSource(100);
var cancelledResult = await TqSdkConnectionWorkflow.ProbeAsync(gateway, new TqSdkCredentials("not-used", "not-used"), cancelled.Token, stages.Add);
var report = new
{
    scope = "WPF-shared preparation workflow with slow no-account bridge; no network login",
    slow_result = new { slow.Success, error = slow.ErrorKind.ToString(), slowElapsedMs = Math.Round(slowElapsed, 1) },
    slow_stages = stages.Take(2).ToArray(),
    cancelled_result = new { cancelledResult.Success, error = cancelledResult.ErrorKind.ToString() },
    bounded_cancelled = cancelledResult.ErrorKind == MarketErrorKind.Cancelled
};
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return !slow.Success && slow.ErrorKind == MarketErrorKind.Authentication && stages.Take(2).SequenceEqual(["dependency_prepare", "network_handshake:surrogate"]) && cancelledResult.ErrorKind == MarketErrorKind.Cancelled ? 0 : 1;
