using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace OsuPlayer.Game.Logging
{
    /// <summary>
    /// 训练界面的机器信息采集：CPU 型号/逻辑线程、系统 CPU 占用、进程内存、CPU 温度。
    ///
    /// 实现取舍（联网核对过）：
    /// - CPU 占用：<c>GetSystemTimes</c> 取两次采样的 idle/kernel/user 差分，无第三方依赖；
    ///   用 PerformanceCounter 需要额外包且首次读取延迟明显，不适合 1 秒级刷新。
    /// - CPU 型号：注册表 <c>HARDWARE\DESCRIPTION\System\CentralProcessor\0</c>，与任务管理器同源。
    /// - CPU 温度：Windows 没有通用 API，唯一不装驱动的方式是 WMI
    ///   <c>root\WMI:MSAcpi_ThermalZoneTemperature</c>（单位 0.1K）。不少笔记本主板不暴露这个
    ///   类，查询还会阻塞几十毫秒，所以放到后台线程、5 秒一次，失败两次后标记为不可用。
    ///   需要更完整的读数（风扇/功耗/核心温度）只能引入 LibreHardwareMonitor 并加载内核驱动
    ///   （要管理员权限），不适合直接塞进播放器。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class SystemMonitor : IDisposable
    {
        private static readonly Process current = Process.GetCurrentProcess();

        private readonly string cpuName;
        private readonly object gate = new();

        private long previousIdle;
        private long previousKernel;
        private long previousUser;
        private bool hasPreviousSample;

        private double cpuUsage;
        private double? temperature;
        private bool temperatureIsAcpiZone;
        private int temperatureFailures;
        private bool temperatureUnavailable;
        private bool temperatureSampling;
        private DateTime nextTemperatureSample = DateTime.MinValue;

        public SystemMonitor()
        {
            cpuName = readCpuName();
        }

        public string CpuName => cpuName;

        public int LogicalProcessors => Environment.ProcessorCount;

        public long WorkingSetBytes => current.WorkingSet64;

        public double? Temperature
        {
            get
            {
                lock (gate)
                    return temperature;
            }
        }

        public bool TemperatureUnavailable
        {
            get
            {
                lock (gate)
                    return temperatureUnavailable;
            }
        }

        /// <summary>
        /// 温度是否来自 ACPI 热区（性能计数器）而不是 WMI 的 MSAcpi_ThermalZoneTemperature。
        /// 前者普通权限可读，但读数是主板热区，可能明显低于 CPU 核心温度，界面要注明。
        /// </summary>
        public bool TemperatureIsAcpiZone
        {
            get
            {
                lock (gate)
                    return temperatureIsAcpiZone;
            }
        }

        /// <summary>每次界面刷新（约 1 秒）调用一次，采集开销只有两次系统调用。</summary>
        public void Sample()
        {
            sampleCpu();

            lock (gate)
            {
                if (temperatureUnavailable || temperatureSampling || DateTime.UtcNow < nextTemperatureSample)
                    return;

                temperatureSampling = true;
                nextTemperatureSample = DateTime.UtcNow.AddSeconds(5);
            }

            _ = Task.Run(sampleTemperature);
        }

        /// <summary>当前 CPU 占用率（0..1），由两次 <see cref="Sample"/> 的差值给出。</summary>
        public double CpuUsage
        {
            get
            {
                lock (gate)
                    return cpuUsage;
            }
        }

        private void sampleCpu()
        {
            if (!GetSystemTimes(out long idle, out long kernel, out long user))
                return;

            lock (gate)
            {
                if (!hasPreviousSample)
                {
                    previousIdle = idle;
                    previousKernel = kernel;
                    previousUser = user;
                    hasPreviousSample = true;
                    return;
                }

                long idleDelta = idle - previousIdle;
                long kernelDelta = kernel - previousKernel;
                long userDelta = user - previousUser;

                previousIdle = idle;
                previousKernel = kernel;
                previousUser = user;

                // kernel 的差分里已经包含 idle。
                long busyDelta = kernelDelta + userDelta - idleDelta;
                long totalDelta = kernelDelta + userDelta;

                if (totalDelta > 0)
                    cpuUsage = Math.Clamp((double)busyDelta / totalDelta, 0, 1);
            }
        }

        private void sampleTemperature()
        {
            try
            {
                (double? reading, bool acpiZone) = readWmiTemperature();

                lock (gate)
                {
                    if (reading is double value)
                    {
                        temperature = value;
                        temperatureIsAcpiZone = acpiZone;
                        temperatureFailures = 0;
                    }
                    else if (++temperatureFailures >= 2)
                    {
                        temperatureUnavailable = true;
                    }
                }
            }
            catch (Exception)
            {
                lock (gate)
                {
                    if (++temperatureFailures >= 2)
                        temperatureUnavailable = true;
                }
            }
            finally
            {
                lock (gate)
                    temperatureSampling = false;
            }
        }

        private static (double? Value, bool AcpiZone) readWmiTemperature()
        {
            double? direct = queryTemperature(@"root\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", "CurrentTemperature");

            if (direct is not null)
                return (direct, false);

            // 部分笔记本（含本机的 Intel Ultra 7 255HX）不允许普通权限读 MSAcpi_ThermalZoneTemperature
            // （返回「拒绝访问」），但同源的性能计数器类可以读，精度同样是 0.1K。
            double? counter = queryTemperature(@"root\CIMV2",
                "SELECT HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation",
                "HighPrecisionTemperature");

            return (counter, true);
        }

        private static double? queryTemperature(string scope, string query, string property)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(scope, query);
                using ManagementObjectCollection results = searcher.Get();
                double best = double.MinValue;

                foreach (ManagementBaseObject item in results)
                {
                    // WMI 返回 0.1 开尔文。
                    double celsius = Convert.ToDouble(item[property]) / 10.0 - 273.15;

                    if (celsius > 10 && celsius < 120 && celsius > best)
                        best = celsius;
                }

                return best == double.MinValue ? null : best;
            }
            catch (Exception)
            {
                // 主板不暴露对应的 WMI 类时会抛 ManagementException / COMException / UnauthorizedAccess。
                return null;
            }
        }

        private static string readCpuName()
        {
            try
            {
                object? value = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString", null);

                if (value is string name && !string.IsNullOrWhiteSpace(name))
                    return name.Trim();
            }
            catch (Exception)
            {
                // 注册表被策略限制时退回环境变量。
            }

            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "未知";
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

        public void Dispose()
        {
        }
    }
}
