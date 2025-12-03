using System.Net;
using ConCore;

namespace ConInfrastructure.Examples
{
    /// <summary>
    /// Demonstrates how to easily switch between real ICMP pings and simulated pings.
    /// </summary>
    public static class RealVsSimulatedExample
    {
        /// <summary>
        /// Runs ping monitoring with either real or simulated factory.
        /// </summary>
        public static async Task RunMonitoring(IPingTransceiverFactory factory, string description)
        {
            Console.WriteLine($"\n=== {description} ===\n");

            var ipAddress = IPAddress.Parse("8.8.8.8");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var transceiver = factory.Create(ipAddress, cts.Token);

            Console.WriteLine($"Monitoring {ipAddress}...\n");

            int successCount = 0;
            int failureCount = 0;
            var latencies = new List<long>();

            for (int i = 1; i <= 10; i++)
            {
                try
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
                }
                catch (Exception ex)
                {
                    failureCount++;
                    Console.WriteLine($"Ping {i:D2}: ? Exception: {ex.Message}");
                }

                await Task.Delay(1000, cts.Token);
            }

            // Display statistics
            var total = successCount + failureCount;
            var successRate = (double)successCount / total * 100;
            var avgLatency = latencies.Any() ? latencies.Average() : 0;
            var minLatency = latencies.Any() ? latencies.Min() : 0;
            var maxLatency = latencies.Any() ? latencies.Max() : 0;
            var stdDev = latencies.Any() ? CalculateStdDev(latencies) : 0;

            Console.WriteLine($"\n{description} Statistics:");
            Console.WriteLine($"  Total Pings:   {total}");
            Console.WriteLine($"  Success:       {successCount} ({successRate:F1}%)");
            Console.WriteLine($"  Failures:      {failureCount}");
            
            if (latencies.Any())
            {
                Console.WriteLine($"  Avg Latency:   {avgLatency:F1}ms");
                Console.WriteLine($"  Min Latency:   {minLatency}ms");
                Console.WriteLine($"  Max Latency:   {maxLatency}ms");
                Console.WriteLine($"  Std Dev:       {stdDev:F1}ms");
            }
        }

        /// <summary>
        /// Demonstrates switching between real and simulated based on configuration.
        /// </summary>
        public static async Task ConfigurableExample(bool useSimulation)
        {
            IPingTransceiverFactory factory;

            if (useSimulation)
            {
                // Use simulation for development/testing
                Console.WriteLine("Using SIMULATED pings (no network required)");
                factory = PingTransceiverFactorySimulated.CreateStableNetwork();
            }
            else
            {
                // Use real ICMP for production
                Console.WriteLine("Using REAL ICMP pings (requires network access)");
                factory = new PingTransceiverFactoryIcmp();
            }

            await RunMonitoring(factory, useSimulation ? "Simulated Network" : "Real Network");
        }

        /// <summary>
        /// Demonstrates running both real and simulated side-by-side for comparison.
        /// </summary>
        public static async Task ComparisonExample()
        {
            Console.WriteLine("=== Real vs Simulated Comparison ===");
            Console.WriteLine("This demonstrates that both implementations use the same interface.\n");

            // Simulated - always works, no network needed
            await RunMonitoring(
                PingTransceiverFactorySimulated.CreateStableNetwork(),
                "Simulated Stable Network"
            );

            Console.WriteLine("\n" + new string('-', 60));

            // Real - requires network access
            try
            {
                await RunMonitoring(
                    new PingTransceiverFactoryIcmp(),
                    "Real ICMP Network"
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nReal ICMP failed (expected in restricted environments): {ex.Message}");
                Console.WriteLine("This is why simulation is useful for development!");
            }
        }

        /// <summary>
        /// Demonstrates creating a factory from configuration/environment.
        /// </summary>
        public static IPingTransceiverFactory CreateFactoryFromConfig(string mode, PingSimulationOptions? options = null)
        {
            return mode.ToLowerInvariant() switch
            {
                "real" or "icmp" or "production" => new PingTransceiverFactoryIcmp(),
                "simulated" or "sim" or "test" => new PingTransceiverFactorySimulated(options ?? new PingSimulationOptions()),
                "perfect" => PingTransceiverFactorySimulated.CreatePerfectNetwork(),
                "stable" => PingTransceiverFactorySimulated.CreateStableNetwork(),
                "unreliable" => PingTransceiverFactorySimulated.CreateUnreliableNetwork(),
                _ => throw new ArgumentException($"Unknown mode: {mode}", nameof(mode))
            };
        }

        /// <summary>
        /// Example showing environment-based factory selection.
        /// </summary>
        public static async Task EnvironmentBasedExample()
        {
            Console.WriteLine("\n=== Environment-Based Factory Selection ===\n");

            // In real application, this would come from configuration or environment variable
            var environment = "Development"; // or "Production"

            IPingTransceiverFactory factory = environment switch
            {
                "Development" or "Testing" => PingTransceiverFactorySimulated.CreateStableNetwork(),
                "Staging" => PingTransceiverFactorySimulated.CreateUnreliableNetwork(), // Test error handling
                "Production" => new PingTransceiverFactoryIcmp(),
                _ => throw new ArgumentException($"Unknown environment: {environment}")
            };

            Console.WriteLine($"Environment: {environment}");
            Console.WriteLine($"Factory: {factory.GetType().Name}");

            await RunMonitoring(factory, $"{environment} Environment");
        }

        /// <summary>
        /// Helper method to calculate standard deviation.
        /// </summary>
        private static double CalculateStdDev(List<long> values)
        {
            if (values.Count < 2)
                return 0;

            var avg = values.Average();
            var sumOfSquares = values.Sum(v => Math.Pow(v - avg, 2));
            return Math.Sqrt(sumOfSquares / (values.Count - 1));
        }

        /// <summary>
        /// Demonstrates A/B testing different simulation profiles.
        /// </summary>
        public static async Task ABTestingExample()
        {
            Console.WriteLine("\n=== A/B Testing Network Profiles ===\n");

            var profiles = new Dictionary<string, PingSimulationOptions>
            {
                ["Fast Datacenter"] = new PingSimulationOptions
                {
                    MeanRoundTripTimeMs = 5,
                    RoundTripTimeStdDevMs = 2,
                    BasePacketLossRate = 0.0001,
                    OutageProbability = 0.0
                },
                ["Typical Internet"] = new PingSimulationOptions
                {
                    MeanRoundTripTimeMs = 50,
                    RoundTripTimeStdDevMs = 20,
                    BasePacketLossRate = 0.01,
                    OutageProbability = 0.001
                },
                ["Slow/Congested"] = new PingSimulationOptions
                {
                    MeanRoundTripTimeMs = 150,
                    RoundTripTimeStdDevMs = 60,
                    BasePacketLossRate = 0.05,
                    OutageProbability = 0.02
                }
            };

            foreach (var (name, options) in profiles)
            {
                var factory = new PingTransceiverFactorySimulated(options);
                await RunMonitoring(factory, name);
                Console.WriteLine();
            }
        }
    }
}