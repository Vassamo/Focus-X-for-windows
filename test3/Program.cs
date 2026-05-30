using Microsoft.Toolkit.Uwp.Notifications;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Windows.UI.Notifications;

class Program
{
    static Dictionary<int, DateTime> TrackedProcesses = new();
    static object LockObject = new();

    static string whitelistFile = "whitelist.txt";
    static TimeSpan MaxTime = TimeSpan.FromMinutes(10);

    static string settingsFile = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory ?? string.Empty,
        "Scripts",
        "settings.txt"
    );

    static int MonitoringIntervalSeconds = 5;

    static Dictionary<string, DateTime> LastToastTime = new();
    static TimeSpan ToastCooldown = TimeSpan.FromMinutes(2);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    static volatile bool MonitoringEnabled = false;
    static HashSet<string> Whitelist = new();
    static readonly object WhitelistLock = new();

    // =========================
    // TOAST SYSTEM (FIXED)
    // =========================

    static Dictionary<string, DateTime> ToastCooldownMap = new();
    //static TimeSpan ToastCooldown = TimeSpan.FromMinutes(1);


    static string? CurrentToastProcessName = null; // <-- KLUCZ FIXA

    static DateTime PauseMonitoringUntil = DateTime.MinValue;
    static DateTime? PauseStartedAt = null;

    static Dictionary<int, TimeSpan> Runtime = new();
    static Dictionary<int, DateTime> LastSeen = new();
    static readonly Queue<string> ToastQueue = new();
    static readonly object ToastLock = new();

    static HashSet<string> ActiveToasts = new();

    static HashSet<int> LimitTriggered = new();

    Dictionary<string, DateTime> ProcessStart = new();

    static void Main()
    {
        LoadSettings();
        ApplyWhitelist(LoadWhitelist());

        StartToastWorker();

        ToastNotificationManagerCompat.OnActivated += toastArgs =>
        {
            Task.Run(() =>
            {
                try
                {
                    var args = toastArgs.Argument
                        .Split(new[] { '&', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => p.Split('='))
                        .Where(p => p.Length == 2)
                        .ToDictionary(p => p[0], p => p[1]);

                    string? action = args.ContainsKey("action") ? args["action"] : null;
                    string? procName = args.ContainsKey("proc") ? args["proc"] : null;

                    if (string.IsNullOrWhiteSpace(procName))
                    {
                        return;
                    }

                    // 🔥 KLUCZ: RESET TIMERA PRZY KAŻDYM INTERAKCIE Z TOASTEM
                    ResetProcessByName(procName);

                    procName = procName.ToLower();

                    if (action == "kill")
                    {
                        var processes = Process.GetProcesses()
                            .Where(p => p.ProcessName.Equals(procName, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        foreach (var p in processes)
                        {
                            try { p.Kill(); } catch { }
                        }

                        new ToastContentBuilder()
                            .AddText("🛑 Process terminated")
                            .AddText($"Closed all instances of {procName}")
                            .Show();
                    }
                    else if (action == "remove")
                    {
                        var whitelist = LoadWhitelist();

                        if (whitelist.Contains(procName))
                        {
                            whitelist.Remove(procName);
                            ApplyWhitelist(whitelist);
                            RemoveProcessFromTracking(procName);

                            new ToastContentBuilder()
                                .AddText("✅ Removed from whitelist")
                                .AddText($"{procName} removed")
                                .Show();
                        }
                    }
                }
                finally
                {
                }
            });
        };

        StartBackgroundMonitor();

        while (true)
        {
            Console.Clear();
            Console.WriteLine("--- Focus X for Windows ---");
            Console.WriteLine();
            Console.WriteLine("1. Manage whitelist");
            Console.WriteLine("2. Show uptime");
            Console.WriteLine("3. Set MaxTime");
            Console.WriteLine("4. Set monitor interval");
            Console.WriteLine("0. Exit");

            Console.WriteLine();
            Console.Write("Choice: ");

            string choice = Console.ReadLine() ?? "";

            switch (choice)
            {
                case "1": ManageWhitelist(); break;
                case "2": ShowProcessUptime(); break;
                case "3": SetMaxTime(); break;
                case "4": SetMonitoringInterval(); break;
                case "0": return;
            }
        }
    }

    // =========================================================
    // MONITOR (FIX ONLY DUPLICATES)
    // =========================================================

    static void StartBackgroundMonitor()
    {
        Task.Run(() =>
        {
            while (true)
            {
                if (DateTime.Now < PauseMonitoringUntil)
                {
                    Thread.Sleep(500);
                    continue;
                }

                HashSet<string> whitelist;
                lock (WhitelistLock)
                    whitelist = new HashSet<string>(Whitelist);

                UpdateMonitoringState(whitelist);

                var now = DateTime.Now;
                var processes = Process.GetProcesses();

                lock (LockObject)
                {
                    foreach (var proc in processes)
                    {
                        try
                        {
                            int pid = proc.Id;
                            string name = proc.ProcessName.ToLower();

                            if (!whitelist.Contains(name))
                                continue;

                            if (!LastSeen.ContainsKey(pid))
                            {
                                LastSeen[pid] = now;
                                Runtime[pid] = TimeSpan.Zero;

                                if (!TrackedProcesses.ContainsKey(pid))
                                    TrackedProcesses[pid] = now;

                                continue;
                            }

                            TimeSpan runtime;

                            try
                            {
                                runtime = DateTime.Now - proc.StartTime;
                            }
                            catch
                            {
                                continue;
                            }

                            if (runtime > MaxTime)
                            {
                                string processName = proc.ProcessName.ToLower();

                                lock (ToastLock)
                                {
                                    if (LimitTriggered.Contains(pid))
                                        continue;

                                    LimitTriggered.Add(pid);

                                    if (ActiveToasts.Contains(processName))
                                        continue;

                                    ActiveToasts.Add(processName);
                                    ToastQueue.Enqueue(processName);
                                }
                            }
                        }
                        catch { }
                    }
                }

                Thread.Sleep(MonitoringIntervalSeconds * 1000);
            }
        });
    }

    // =========================================================
    // TOAST WORKER (FIXED)
    // =========================================================

    static void StartToastWorker()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                string? next = null;

                lock (ToastLock)
                {
                    if (ToastQueue.Count > 0)
                        next = ToastQueue.Dequeue();
                }

                if (next != null)
                    ShowToast(next);

                await Task.Delay(300);
            }
        });
    }



    // =========================================================
    // TOAST (FIXED DUPLICATES)
    // =========================================================

    static void ShowToast(string processName)
    {
        try
        {
            PauseMonitoring(2);

            var procs = Process.GetProcesses()
                .Where(p => p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => GetStartTimeSafe(p))   
                .ToList();

            if (procs.Count == 0)
                return;

            var proc = procs.First();

            // =========================
            // ICON FIX 
            // =========================
            string? iconPath = null;

            try
            {
                var exePath = proc.MainModule?.FileName;

                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    var icon = Icon.ExtractAssociatedIcon(exePath);

                    if (icon != null)
                    {
                        iconPath = Path.Combine(Path.GetTempPath(), $"{processName}.png");

                        using var stream = new FileStream(iconPath, FileMode.Create);
                        icon.ToBitmap().Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
            }
            catch
            {
           
            }

            // =========================
            // TOAST BUILDER
            // =========================
            var builder = new ToastContentBuilder()
                .SetToastDuration(ToastDuration.Long)
                .AddText("⏰ Time limit exceeded")
                .AddText($"{processName} is running too long");

            // 👇 IKONA
            if (iconPath != null)
            {
                builder.AddAppLogoOverride(new Uri(iconPath));
            }

            builder
                .AddButton(new ToastButton().SetContent("🛑 Kill")
                    .AddArgument("action", "kill")
                    .AddArgument("proc", processName))
                .AddButton(new ToastButton().SetContent("🧹 Remove")
                    .AddArgument("action", "remove")
                    .AddArgument("proc", processName));

            var toast = new ToastNotification(builder.GetToastContent().GetXml());

            toast.Dismissed += (s, e) =>
            {
                lock (ToastLock)
                {
                    ActiveToasts.Remove(processName);
                }

                ResetProcessByName(processName);
            };

            ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);
        }
        catch
        {
            lock (ToastLock)
                ActiveToasts.Remove(processName);
        }
    }




    // =========================================================
    // WHITELIST
    // =========================================================

    static HashSet<string> LoadWhitelist()
    {
        return File.Exists(whitelistFile)
            ? File.ReadAllLines(whitelistFile)
                .Select(x => x.Trim().ToLower())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet()
            : new HashSet<string>();
    }


    static DateTime GetStartTimeSafe(Process p)
    {
        try { return p.StartTime; }
        catch { return DateTime.MaxValue; }
    }

    static void SaveWhitelist(HashSet<string> whitelist)
    {
        File.WriteAllLines(
            whitelistFile,
            whitelist.OrderBy(x => x));
    }

    static void ApplyWhitelist(HashSet<string> newList)
    {
        var normalized = newList
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.ToLower())
            .ToHashSet();

        lock (WhitelistLock)
        {
            Whitelist = normalized;
        }

        SaveWhitelist(normalized);

        UpdateMonitoringState(normalized);
    }

    static void RemoveProcessFromTracking(string name)
    {
        lock (LockObject)
        {
            var keys = TrackedProcesses
                .Where(x =>
                {
                    try
                    {
                        return Process.GetProcessById(x.Key)
                            .ProcessName
                            .Equals(
                                name,
                                StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .Select(x => x.Key)
                .ToList();

            foreach (var k in keys)
            {
                TrackedProcesses.Remove(k);
            }
        }

        lock (LastToastTime)
        {
            LastToastTime.Remove(name);
        }
    }

    static void UpdateMonitoringState(HashSet<string> whitelist)
    {
        MonitoringEnabled = whitelist.Count > 0;
    }

    // =========================================================
    // MENU FUNCTIONS
    // =========================================================

    static void ManageWhitelist()
    {
        Console.Clear();

        var whitelist = LoadWhitelist();

        var allProcesses = Process.GetProcesses()
            .Where(IsRealUserApp)
            .Select(p => p.ProcessName.ToLower())
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        Console.WriteLine("Applications:\n");

        for (int i = 0; i < allProcesses.Count; i++)
        {
            string name = allProcesses[i];

            string state =
                whitelist.Contains(name)
                    ? "[+]"
                    : "[ ]";

            Console.WriteLine($"{i + 1}. {state} {name}");
        }

        Console.WriteLine();
        Console.Write("Numbers (e.g. 1,4,5): ");

        string input = Console.ReadLine() ?? "";

        var indexes = input.Split(',')
            .Select(x =>
                int.TryParse(x.Trim(), out int n)
                    ? n - 1
                    : -1)
            .Where(x => x >= 0 && x < allProcesses.Count)
            .ToList();

        foreach (var i in indexes)
        {
            string name = allProcesses[i];

            if (whitelist.Contains(name))
            {
                whitelist.Remove(name);

                RemoveProcessFromTracking(name);

                Console.WriteLine($"[-] Removed {name}");
            }
            else
            {
                whitelist.Add(name);

                Console.WriteLine($"[+] Added {name}");
            }
        }

        ApplyWhitelist(whitelist);

        Console.WriteLine();
        Console.WriteLine("Done.");

        Console.ReadLine();
    }

    static void ShowProcessUptime()
    {
        Console.Clear();

        Console.WriteLine("Press ESC to exit\n");

        bool running = true;

        while (running)
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);

                if (key.Key == ConsoleKey.Escape)
                {
                    running = false;
                }
            }

            var processes = Process.GetProcesses();

            Console.Clear();

            Console.WriteLine("Press ESC to exit\n");

            lock (LockObject)
            {
                foreach (var proc in processes)
                {
                    try
                    {
                        if (!TrackedProcesses.ContainsKey(proc.Id))
                            continue;

                        var uptime =
    DateTime.Now - TrackedProcesses[proc.Id];


                        Console.WriteLine(
                            $"{proc.ProcessName} ({proc.Id}) | {uptime:hh\\:mm\\:ss}");
                    }
                    catch
                    {
                    }
                }
            }

            Thread.Sleep(500);
        }
    }

    static void SetMaxTime()
    {
        Console.Clear();

        Console.WriteLine($"Current: {MaxTime}");
        Console.Write("New value (30s / 5m / 1h): ");

        string input =
            Console.ReadLine()?.Trim().ToLower() ?? "";

        try
        {
            char unit = input[^1];

            string num = input[..^1];

            double value = double.Parse(num);

            MaxTime = unit switch
            {
                's' => TimeSpan.FromSeconds(value),
                'm' => TimeSpan.FromMinutes(value),
                'h' => TimeSpan.FromHours(value),
                _ => throw new Exception()
            };

            SaveSettings();

            Console.WriteLine("Saved.");
        }
        catch
        {
            Console.WriteLine("Invalid format.");
        }

        Console.ReadLine();
    }

    static void SetMonitoringInterval()
    {
        Console.Clear();

        Console.WriteLine(
            $"Current: {MonitoringIntervalSeconds}s");

        Console.Write("New value: ");

        if (int.TryParse(Console.ReadLine(), out int value))
        {
            MonitoringIntervalSeconds = Math.Max(1, value);

            SaveSettings();

            Console.WriteLine("Saved.");
        }

        Console.ReadLine();
    }

    // =========================================================
    // SETTINGS
    // =========================================================

    static void LoadSettings()
    {
        if (!File.Exists(settingsFile))
            return;

        foreach (var line in File.ReadAllLines(settingsFile))
        {
            if (line.StartsWith("MaxTime="))
            {
                if (TimeSpan.TryParse(
                    line.Replace("MaxTime=", ""),
                    out var ts))
                {
                    MaxTime = ts;
                }
            }

            if (line.StartsWith("MonitoringInterval="))
            {
                if (int.TryParse(
                    line.Replace("MonitoringInterval=", ""),
                    out int sec))
                {
                    MonitoringIntervalSeconds = sec;
                }
            }
        }
    }

    static void SaveSettings()
    {
        string? dir = Path.GetDirectoryName(settingsFile);

        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir!);
        }

        File.WriteAllLines(settingsFile, new[]
        {
            $"MaxTime={MaxTime}",
            $"MonitoringInterval={MonitoringIntervalSeconds}"
        });
    }

    // =========================================================
    // APP FILTER
    // =========================================================

    static bool IsRealUserApp(Process p)
    {
        try
        {
            if (p.SessionId == 0)
                return false;

            if (p.MainWindowHandle == IntPtr.Zero)
                return false;

            if (!IsWindowVisible(p.MainWindowHandle))
                return false;

            string? path = p.MainModule?.FileName;

            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (path.StartsWith(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.Windows),
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    static void ResetProcessByName(string name)
    {
        lock (LockObject)
        {
            foreach (var pid in TrackedProcesses.Keys.ToList())
            {
                try
                {
                    var p = Process.GetProcessById(pid);

                    if (p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        TrackedProcesses[pid] = DateTime.Now;
                        LimitTriggered.Remove(pid);
                    }
                }
                catch { }
            }
        }
    }


    static void PauseMonitoring(int seconds = 30)
    {
        if (PauseStartedAt == null)
            PauseStartedAt = DateTime.Now;

        PauseMonitoringUntil = DateTime.Now.AddSeconds(seconds);
    }
}