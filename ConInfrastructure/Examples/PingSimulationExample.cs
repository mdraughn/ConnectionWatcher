using System.Net;
using ConCore;

namespace ConInfrastructure.Examples
{
    /// <summary>
    /// Examples demonstrating the use of simulated ping transceivers.
    /// </summary>
    public static class PingSimulationExample
    {
        /// <summary>
        /// Demonstrates basic simulation with default settings.
        /// </summary>
        public static async Task BasicSimulationExample()
        {
            Console.WriteLine("=== Basic Simulation Example ===\n");

            var factory = new PingTransceiverFactorySimulated();
            var ipAddress = IPAddress.Parse("192.0.2.1"); // TEST-NET-1
            
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var transceiver = factory.Create(ipAddress, cts.Token);

            Console.WriteLine($"Simulating pings to {ipAddress} with default settings...\n");

            for (int i = 1; i <= 10; i++)
            {
                var result = await transceiver.PingAsync(5000);
                var status = result.Success ? "?" : "?";
                var timeInfo = result.Success ? $"{result.RoundtripTime}ms" : result.Message;
                
                Console.WriteLine($"Ping {i:D2}: {status} {timeInfo}");
                
                await Task.Delay(1000, cts.Token);
            }
        }

        /// <summary>
        /// Demonstrates perfect network simulation.
        /// </summary>
        public static async Task PerfectNetworkExample()
        {
            Console.WriteLine("\n=== Perfect Network Example ===\n");

            var factory = PingTransceiverFactorySimulated.CreatePerfectNetwork(latencyMs: 15.0);
            var ipAddress = IPAddress.Parse("8.8.8.8");
            
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var transceiver = factory.Create(ipAddress, cts.Token);

            Console.WriteLine($"Simulating perfect network to {ipAddress}...");
            Console.WriteLine("Expected: Consistent ~15ms latency, no packet loss\n");

            var latencies = new List<long>();

            for (int i = 1; i <= 10; i++)
            {
                var result = await transceiver.PingAsync(5000);
                latencies.Add(result.RoundtripTime);
                
                Console.WriteLine($"Ping {i:D2}: {result.RoundtripTime}ms");
                await Task.Delay(500, cts.Token);
            }

            var avgLatency = latencies.Average();
            var maxLatency = latencies.Max();
            var minLatency = latencies.Min();
            
            Console.WriteLine($"\nStatistics: Avg={avgLatency:F1}ms, Min={minLatency}ms, Max={maxLatency}ms");
        }

        /// <summary>
        /// Demonstrates unreliable network with packet loss and outages.
        /// </summary>
        public static async Task UnreliableNetworkExample()
        {
            Console.WriteLine("\n=== Unreliable Network Example ===\n");

            var factory = PingTransceiverFactorySimulated.CreateUnreliableNetwork();
            var ipAddress = IPAddress.Parse("192.0.2.100");
            
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var transceiver = factory.Create(ipAddress, cts.Token);

            Console.WriteLine($"Simulating unreliable network to {ipAddress}...");
            Console.WriteLine("Expected: High latency, packet loss, possible outages\n");

            int successCount = 0;
            int failureCount = 0;
            var latencies = new List<long>();

            for (int i = 1; i <= 30; i++)
            {
                var result = await transceiver.PingAsync(5000);
                
                if (result.Success)
                {
                    successCount++;
                    latencies.Add(result.RoundtripTime);
                    Console.WriteLine($"Ping {i:D2}: ? {result.RoundtripTime}ms");
                }
                else
                {
                    failureCount++;
                    Console.WriteLine($"Ping {i:D2}: ? {result.Message}");
                }
                
                await Task.Delay(1000, cts.Token);
            }

            var successRate = (double)successCount / (successCount + failureCount) * 100;
            var avgLatency = latencies.Any() ? latencies.Average() : 0;
            
            Console.WriteLine($"\nStatistics:");
            Console.WriteLine($"  Success Rate: {successRate:F1}% ({successCount}/{successCount + failureCount})");
            Console.WriteLine($"  Average Latency: {avgLatency:F1}ms");
        }

        /// <summary>
        /// Demonstrates custom simulation configuration using the builder.
        /// </summary>
        public static async Task CustomSimulationExample()
        {
            Console.WriteLine("\n=== Custom Simulation Example ===\n");

            // Create a custom simulation: moderate latency, occasional packet loss, rare outages
            var factory = PingTransceiverFactorySimulated.CreateCustom()
                .WithLatency(meanMs: 50, stdDevMs: 15)
                .WithPacketLoss(0.02) // 2% packet loss
                .WithOutages(probability: 0.005, meanDurationMs: 10000, stdDevMs: 3000)
                .WithOutagePacketLoss(0.9) // 90% loss during outage
                .WithRandomSeed(42) // Reproducible results
                .Build();

            var ipAddress = IPAddress.Parse("203.0.113.1"); // TEST-NET-3
            
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            using var transceiver = factory.Create(ipAddress, cts.Token);

            Console.WriteLine($"Simulating custom network profile to {ipAddress}...");
            Console.WriteLine("Profile: 50±15ms latency, 2% base loss, rare outages\n");

            for (int i = 1; i <= 20; i++)
            {
                var result = await transceiver.PingAsync(5000);
                var status = result.Success ? "?" : "?";
                var info = result.Success ? $"{result.RoundtripTime}ms" : result.Message;
                
                Console.WriteLine($"Ping {i:D2}: {status} {info}");
                await Task.Delay(1000, cts.Token);
            }
        }

        /// <summary>
        /// Demonstrates simulation with outage detection.
        /// </summary>
        public static async Task OutageDetectionExample()
        {
            Console.WriteLine("\n=== Outage Detection Example ===\n");

            var options = new PingSimulationOptions
            {
                MeanRoundTripTimeMs = 20,
                RoundTripTimeStdDevMs = 5,
                BasePacketLossRate = 0.01,
                OutageProbability = 0.05, // High probability for demonstration
                MeanOutageDurationMs = 8000,
                OutageDurationStdDevMs = 2000,
                OutagePacketLossRate = 1.0,
                RandomSeed = 123
            };

            var factory = new PingTransceiverFactorySimulated(options);
            var ipAddress = IPAddress.Parse("198.51.100.1"); // TEST-NET-2
            
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            using var transceiver = factory.Create(ipAddress, cts.Token) as PingTransceiverSimulated;

            Console.WriteLine($"Monitoring {ipAddress} for outages...\n");

            for (int i = 1; i <= 40; i++)
            {
                var state = transceiver?.GetSimulationState();
                var result = await transceiver!.PingAsync(5000);
                
                var status = result.Success ? "?" : "?";
                var info = result.Success ? $"{result.RoundtripTime}ms" : result.Message;
                var outageInfo = state?.IsInOutage == true 
                    ? $" [OUTAGE: {state.OutageRemainingTime?.TotalSeconds:F1}s remaining]" 
                    : "";
                
                Console.WriteLine($"Ping {i:D2}: {status} {info}{outageInfo}");
                await Task.Delay(1000, cts.Token);
            }
        }

        /// <summary>
        /// Demonstrates comparing multiple network profiles side-by-side.
        /// </summary>
        public static async Task ComparisonExample()
        {
            Console.WriteLine("\n=== Network Profile Comparison ===\n");

            var profiles = new Dictionary<string, IPingTransceiverFactory>
            {
                ["Perfect"] = PingTransceiverFactorySimulated.CreatePerfectNetwork(10),
                ["Stable"] = PingTransceiverFactorySimulated.CreateStableNetwork(),
                ["Unreliable"] = PingTransceiverFactorySimulated.CreateUnreliableNetwork()
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            foreach (var (name, factory) in profiles)
            {
                Console.WriteLine($"Testing {name} Network:");
                
                var ipAddress = IPAddress.Parse($"192.0.2.{profiles.Keys.ToList().IndexOf(name) + 1}");
                using var transceiver = factory.Create(ipAddress, cts.Token);

                int successCount = 0;
                var latencies = new List<long>();

                for (int i = 0; i < 10; i++)
                {
                    var result = await transceiver.PingAsync(5000);
                    if (result.Success)
                    {
                        successCount++;
                        latencies.Add(result.RoundtripTime);
                    }
                    await Task.Delay(500, cts.Token);
                }

                var successRate = (double)successCount / 10 * 100;
                var avgLatency = latencies.Any() ? latencies.Average() : 0;
                
                Console.WriteLine($"  Success: {successRate}%, Avg Latency: {avgLatency:F1}ms\n");
            }
        }
    }
}