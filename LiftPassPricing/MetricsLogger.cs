using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiftPassPricing
{
    /// <summary>
    /// Telt lopende en afgehandelde requests. Registreer met app.UseRequestCounter().
    /// Code hiervoor is eigenlijk niet nodig, met dotnet-counters kun je ook veel statistieken standaard in zien
    /// dit werkt echter out-of-the box
    /// </summary>
    public static class RequestCounter
    {
        public static long InFlight;
        public static long Total;

        public static IApplicationBuilder UseRequestCounter(this IApplicationBuilder app)
        {
            return app.Use(async (context, next) =>
            {
                Interlocked.Increment(ref InFlight);
                try
                {
                    await next();
                }
                finally
                {
                    Interlocked.Decrement(ref InFlight);
                    Interlocked.Increment(ref Total);
                }
            });
        }
    }

    /// <summary>
    /// Logt elke 5 seconden CPU, geheugen, threadpool-wachtrij en requests per seconde.
    /// Registreer met builder.Services.AddHostedService&lt;MetricsLogger&gt;().
    /// </summary>
    public class MetricsLogger : BackgroundService
    {
        private readonly ILogger<MetricsLogger> logger;

        public MetricsLogger(ILogger<MetricsLogger> logger)
        {
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var process = Process.GetCurrentProcess();
            var lastCpu = process.TotalProcessorTime;
            var lastTime = DateTime.UtcNow;
            long lastTotal = 0;

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                process.Refresh();
                var now = DateTime.UtcNow;
                var cpu = process.TotalProcessorTime;
                var elapsed = now - lastTime;

                var cpuPercent = (cpu - lastCpu).TotalMilliseconds
                                 / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;
                var total = Interlocked.Read(ref RequestCounter.Total);
                var requestsPerSecond = (total - lastTotal) / elapsed.TotalSeconds;

                logger.LogInformation(
                    "CPU {Cpu,3:F0}% | Mem {Mem,4} MB | GC heap {Heap,4} MB | Threads {Threads,3} | " +
                    "Queue {Queue,4} | In-flight {InFlight,4} | {Rps,6:F0} req/s",
                    cpuPercent,
                    process.WorkingSet64 / 1024 / 1024,
                    GC.GetTotalMemory(false) / 1024 / 1024,
                    ThreadPool.ThreadCount,
                    ThreadPool.PendingWorkItemCount,
                    Interlocked.Read(ref RequestCounter.InFlight),
                    requestsPerSecond);

                lastCpu = cpu;
                lastTime = now;
                lastTotal = total;
            }
        }
    }
}