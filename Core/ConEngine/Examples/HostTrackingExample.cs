using System.Net;
using ConEngine;
using ConEngine.Tracking;
using ConInfrastructure;
using Microsoft.Extensions.Logging;

namespace ConEngine.Examples
{
    /// <summary>
    /// Examples demonstrating the use of host tracking in WatcherEngine.
    /// </summary>
    public static class HostTrackingExample
    {
        /// <summary>
        /// Demonstrates basic host tracking with the WatcherEngine.
        /// </summary>
        public static async Task BasicTrackingExample()
        {
            Console.WriteLine("=== Basic Host Tracking Example ===\n");

            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });

            var factory = PingTransceiverFactorySimulated.CreateStableNetwork();
            var engine = new WatcherEngine(TimeProvider.System, factory, loggerFactory);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            
            await engine.StartAsync(cts.Token);

            // Start watching multiple hosts
            var hosts = new[]
            {
                IPAddress.Parse("8.8.8.8"),
                IPAddress.Parse("1.1.1.1"),
                IPAddress.Parse("9.9.9.9")
            };

            foreach (var host in hosts)
            {
                engine.StartWatching(host);
                Console.WriteLine($"Started watching {host}");
            }

            Console.WriteLine($"\nTracking {engine.TrackedHostCount} hosts...\n");

            // Let it run for a while to collect data
            await Task.Delay(TimeSpan.FromSeconds(15), cts.Token);

            // Display tracking information
            Console.WriteLine("\n=== Host Tracking Summary ===\n");
            foreach (var summary in engine.GetAllTrackerSummaries())
            {
                Console.WriteLine($"Host: {summary.HostAddress}");
                Console.WriteLine($"  Overall Success: {summary.SuccessRate:F1}% ({summary.SuccessfulPingCount}/{summary.TotalPingCount})");
                Console.WriteLine($"  Recent Success: {summary.RecentSuccessRate:F1}% (last {summary.HistoryCount} pings)");
                Console.WriteLine($"  Average RTT: {summary.AverageRoundtripTime:F1}ms");
                Console.WriteLine($"  Min/Max RTT: {summary.MinimumRoundtripTime}ms / {summary.MaximumRoundtripTime}ms");
                Console.WriteLine($"  Consecutive: {(summary.ConsecutiveSuccessCount > 0 ? $"{summary.ConsecutiveSuccessCount} successes" : $"{summary.ConsecutiveFailureCount} failures")}");
                Console.WriteLine();
            }

            await engine.StopAsync(cts.Token);
        }

        /// <summary>
        /// Demonstrates detailed history viewing.
        /// </summary>
        public static async Task DetailedHistoryExample()
        {
            Console.WriteLine("=== Detailed History Example ===\n");

            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Warning);
            });

            var factory = PingTransceiverFactorySimulated.CreateUnreliableNetwork();
            var engine = new WatcherEngine(TimeProvider.System, factory, loggerFactory);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            
            await engine.StartAsync(cts.Token);

            var testHost = IPAddress.Parse("192.0.2.1");
            engine.StartWatching(testHost);

            Console.WriteLine($"Watching {testHost} with unreliable network simulation...\n");

            await Task.Delay(TimeSpan.FromSeconds(15), cts.Token);

            // Get the tracker for detailed examination
            var tracker = engine.GetTracker(testHost);
            if (tracker != null)
            {
                Console.WriteLine($"\n=== Detailed History for {testHost} ===\n");
                Console.WriteLine($"Tracking started: {tracker.TrackingStarted:HH:mm:ss}");
                Console.WriteLine($"Total pings: {tracker.TotalPingCount}");
                Console.WriteLine($"History size: {tracker.HistoryCount}/{tracker.HistoryCapacity}\n");

                Console.WriteLine("Recent history (newest to oldest):");
                var history = tracker.GetHistoryReversed();
                foreach (var entry in history)
                {
                    Console.WriteLine($"  {entry}");
                }

                Console.WriteLine($"\nLatest result:");
                if (tracker.LatestResult != null)
                {
                    Console.WriteLine($"  Status: {(tracker.LatestSuccess == true ? "Success" : "Failed")}");
                    Console.WriteLine($"  Time: {tracker.LatestRoundtripTime}ms");
                    Console.WriteLine($"  Message: {tracker.LatestMessage ?? "N/A"}");
                    Console.WriteLine($"  Timestamp: {tracker.LatestTimestamp:HH:mm:ss.fff}");
                }

                Console.WriteLine($"\nStatistics:");
                Console.WriteLine($"  Consecutive results: {(tracker.ConsecutiveSuccessCount > 0 ? $"{tracker.ConsecutiveSuccessCount} successes" : $"{tracker.ConsecutiveFailureCount} failures")}");
            }

            await engine.StopAsync(cts.Token);
        }

        /// <summary>
        /// Demonstrates real-time monitoring of host statistics.
        /// </summary>
        public static async Task RealTimeMonitoringExample()
        {
            Console.WriteLine("=== Real-Time Monitoring Example ===\n");

            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Warning);
            });

            var factory = PingTransceiverFactorySimulated.CreateStableNetwork();
            var engine = new WatcherEngine(TimeProvider.System, factory, loggerFactory);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            
            await engine.StartAsync(cts.Token);

            var monitoredHost = IPAddress.Parse("8.8.8.8");
            engine.StartWatching(monitoredHost);

            Console.WriteLine($"Real-time monitoring of {monitoredHost}");
            Console.WriteLine("Press Ctrl+C to stop\n");

            // Monitor for 25 seconds, updating display every 2 seconds
            for (int i = 0; i < 12; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);

                var tracker = engine.GetTracker(monitoredHost);
                if (tracker != null)
                {
                    Console.Clear();
                    Console.WriteLine($"=== Real-Time Monitor: {monitoredHost} ===\n");
                    Console.WriteLine($"Current Time: {DateTime.Now:HH:mm:ss}");
                    Console.WriteLine($"Uptime: {(DateTime.UtcNow - tracker.TrackingStarted).TotalSeconds:F0}s\n");

                    Console.WriteLine($"Latest: {(tracker.LatestSuccess == true ? "?" : "?")} {tracker.LatestRoundtripTime}ms");
                    Console.WriteLine($"Total Pings: {tracker.TotalPingCount}");
                    Console.WriteLine($"Success Rate: {tracker.SuccessRate:F1}%");
                    Console.WriteLine($"Recent Success Rate: {tracker.RecentSuccessRate:F1}% (last {tracker.HistoryCount})");
                    
                    if (tracker.AverageRoundtripTime.HasValue)
                    {
                        Console.WriteLine($"Average RTT: {tracker.AverageRoundtripTime:F1}ms");
                        Console.WriteLine($"RTT Range: {tracker.MinimumRoundtripTime}ms - {tracker.MaximumRoundtripTime}ms");
                    }

                    Console.WriteLine($"\nConsecutive: {(tracker.ConsecutiveSuccessCount > 0 ? $"{tracker.ConsecutiveSuccessCount} successes ?" : $"{tracker.ConsecutiveFailureCount} failures ?")}");

                    Console.WriteLine("\nRecent History:");
                    foreach (var entry in tracker.GetHistoryReversed().Take(5))
                    {
                        var status = entry.Success ? "?" : "?";
                        var time = entry.Success ? $"{entry.RoundtripTime}ms" : entry.Message ?? "Failed";
                        Console.WriteLine($"  {entry.Timestamp:HH:mm:ss} {status} {time}");
                    }
                }
            }

            await engine.StopAsync(cts.Token);
        }

        /// <summary>
        /// Demonstrates tracking management operations.
        /// </summary>
        public static async Task TrackingManagementExample()
        {
            Console.WriteLine("=== Tracking Management Example ===\n");

            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });

            var factory = PingTransceiverFactorySimulated.CreateStableNetwork();
            var engine = new WatcherEngine(TimeProvider.System, factory, loggerFactory);

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            
            await engine.StartAsync(cts.Token);

            // Add hosts
            var host1 = IPAddress.Parse("8.8.8.8");
            var host2 = IPAddress.Parse("1.1.1.1");
            var host3 = IPAddress.Parse("9.9.9.9");

            engine.StartWatching(host1);
            engine.StartWatching(host2);
            engine.StartWatching(host3);

            Console.WriteLine($"Tracking {engine.TrackedHostCount} hosts\n");

            await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);

            // Stop watching one host but keep tracking data
            engine.StopWatching(host2);
            Console.WriteLine($"\nStopped watching {host2}");
            Console.WriteLine($"Still tracking {engine.TrackedHostCount} hosts\n");

            // View tracker even after stopping
            var tracker2 = engine.GetTracker(host2);
            if (tracker2 != null)
            {
                Console.WriteLine($"Tracker for {host2} still exists:");
                Console.WriteLine($"  Total pings: {tracker2.TotalPingCount}");
                Console.WriteLine($"  Success rate: {tracker2.SuccessRate:F1}%\n");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);

            // Remove tracking data for a host
            engine.RemoveTracking(host2);
            Console.WriteLine($"Removed tracking for {host2}");
            Console.WriteLine($"Now tracking {engine.TrackedHostCount} hosts\n");

            // Clear all tracking
            Console.WriteLine("Clearing all tracking data...");
            engine.ClearAllTracking();
            Console.WriteLine($"Tracked hosts: {engine.TrackedHostCount}");

            await engine.StopAsync(cts.Token);
        }

        /// <summary>
        /// Demonstrates comparing multiple host statistics.
        /// </summary>
        public static async Task ComparisonExample()
        {
            Console.WriteLine("=== Host Comparison Example ===\n");

            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Warning);
            });

            // Use different simulation profiles for different hosts
            var factory = PingTransceiverFactorySimulated.CreateStableNetwork();
            var engine = new WatcherEngine(TimeProvider.System, factory, loggerFactory);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            
            await engine.StartAsync(cts.Token);

            var hosts = new[]
            {
                IPAddress.Parse("8.8.8.8"),
                IPAddress.Parse("1.1.1.1"),
                IPAddress.Parse("9.9.9.9"),
                IPAddress.Parse("208.67.222.222")
            };

            foreach (var host in hosts)
            {
                engine.StartWatching(host);
            }

            Console.WriteLine($"Watching {hosts.Length} hosts...\n");

            await Task.Delay(TimeSpan.FromSeconds(20), cts.Token);

            // Compare all hosts
            Console.WriteLine("=== Host Comparison ===\n");
            Console.WriteLine($"{"Host",-20} {"Success",-10} {"Avg RTT",-10} {"Min/Max",-15} {"Consecutive",-15}");
            Console.WriteLine(new string('-', 75));

            foreach (var summary in engine.GetAllTrackerSummaries().OrderBy(s => s.HostAddress.ToString()))
            {
                var consecutive = summary.ConsecutiveSuccessCount > 0 
                    ? $"{summary.ConsecutiveSuccessCount} ?" 
                    : $"{summary.ConsecutiveFailureCount} ?";
                
                var minMax = summary.MinimumRoundtripTime.HasValue 
                    ? $"{summary.MinimumRoundtripTime}/{summary.MaximumRoundtripTime}"
                    : "N/A";

                Console.WriteLine($"{summary.HostAddress,-20} {summary.RecentSuccessRate,6:F1}%   {summary.AverageRoundtripTime,6:F1}ms  {minMax,-15} {consecutive,-15}");
            }

            await engine.StopAsync(cts.Token);
        }
    }
}