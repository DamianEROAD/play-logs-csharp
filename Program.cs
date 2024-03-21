using NDesk.Options;
using play_logs;
using System.Net;
using System.Net.Sockets;

class Program
{
    class Options
    {
        public int opThreads;
        public string opHost = string.Empty;
        public int opPortBase;
        public double opRate;
    }

    static OptionSet optionsList = [];

    static void Main(string[] args)
    {
        var emptyOptions = new Options
        {
            opThreads = 20,
            opHost = "localhost",
            opPortBase = 1490,
            opRate = 100
        };

        optionsList = new OptionSet
        {
            { "h|hostname", "Host name of ingeniserver, default is localhost", (string host) => emptyOptions.opHost = host },
            { "p|port-base", "Base port number of ingeniserver, default is 1490", (int port) => emptyOptions.opPortBase = port },
            { "t|threads", "Number of threads, default is " + emptyOptions.opThreads, (int threads) => emptyOptions.opThreads = threads },
            { "r|rate", "Number of logs to send per second - overall, not per thread, default is " + emptyOptions.opRate, (double rate) => emptyOptions.opRate = rate }
        };

        List<string> os;
        string directory;

        try
        {
            var extraArgs = optionsList.Parse(args);
            os = extraArgs.Take(extraArgs.Count - 1).ToList();
            directory = extraArgs.Last();
        }
        catch (OptionException e)
        {
            Console.Write("Error: ");
            Console.WriteLine(e.Message);
            Console.WriteLine("Try 'getOptExample --help' for more information.");
            return;
        }

        if (os.Count == 0 || directory == null)
        {
            Console.WriteLine("Missing required argument(s).");
            Console.WriteLine("Try 'getOptExample --help' for more information.");
            return;
        }

        Dictionary<string, List<string>> files;
        List<string> errs;

        try
        {
            List<string> filesList = Directory.GetFiles(directory, "*.log")
                                              .Where(fn => !fn.EndsWith(".done"))
                                              .ToList();
            var opts = new EmptyOptions(); // Define the empty options class
            files = OrganizeBySerialNo(filesList);
            PlayLogs(directory, opts, files);
        }
        catch (Exception e)
        {
            Console.WriteLine("Error: " + e.Message);
            return;
        }
    }

    static void Usage()
    {
        Console.WriteLine("Play ingeniserver logs as if they are new logs being sent by tmus to an ingeniserver");
        Console.WriteLine();
        Console.WriteLine("Note: The identity certificates used are generated ones, which means that");
        Console.WriteLine("identities must be deleted if you want to use real units on the same");
        Console.WriteLine("ingeniserver. Otherwise, the ingeniserver will see the units as impostors.");
        Console.WriteLine();
        Console.WriteLine("Usage: play-logs-csharp <options> <directory>");
        Console.WriteLine("    directory is the directory containing log files which must contain '.log' within");
        Console.WriteLine("    the name and they must start with '<serial_no>-', for example");
        Console.WriteLine("    060115061200508-window.log.gz");
        Console.WriteLine("    Files may be gzipped or not");
        Console.WriteLine(optionsList.ToString());

        Environment.Exit(1);
    }

    static string? SerialNoOfFilename(string fn)
    {
        var serial = string.Concat(fn.TakeWhile(char.IsDigit));
        if (serial.Length == 0 || !fn[serial.Length..].StartsWith('-'))
        {
            return null;
        }

        return serial;
    }

    static async Task<TMUIdent> GetIdentity(Timers timers, string serialNo)
    {
        var dir = "play-logs.identities";
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var sslCtxTask = GetClientSSLCTX(timers, dir, serialNo);
        var sslCtx = await sslCtxTask;
        var tmuIdent = new TMUIdent(sslCtx, serialNo, "serial no " + serialNo);
        return tmuIdent;
    }

    static async Task<object> GetClientSSLCTX(Timers timers, string dir, string serialNo)
    {
        // Implement the logic to get the client SSL context here
        return null;
    }

    static Dictionary<string, List<string>> OrganizeBySerialNo(List<string> fns)
    {
        var m = new Dictionary<string, List<string>>();
        foreach (var fn in fns)
        {
            var serial = SerialNoOfFilename(fn);
            if (serial != null)
            {
                if (!m.ContainsKey(serial))
                    m[serial] = [];
                m[serial].Add(fn);
            }
            else
            {
                Console.WriteLine($"WARNING: can't extract the serial number from filename: {fn}");
            }
        }
        return m;
    }

    static async Task PlayLogs(string directory, Options opts, Dictionary<string, List<string>> files)
    {
        var serverStream = await ResolveAddressAsync(AddressFamily.Unspecified, ProtocolType.Tcp, opts.opHost, (opts.opPortBase + 3).ToString());
        var serverDatagram = await ResolveAddressAsync(AddressFamily.InterNetwork, ProtocolType.Udp, opts.opHost, (opts.opPortBase + 3).ToString());

        var capture = false;
        var ctx = new TMUContext(serverStream, serverDatagram, "simulated-tmu-dict.xml", capture);

        ManualResetEvent mvToTerminate = new(false); // Assume this is a bool variable
        Console.WriteLine("press ENTER to terminate cooperatively");
        Task.Run(() =>
        {
            Console.ReadLine();
            mvToTerminate.Set();
        });

        await ScheduleThreads(directory, opts, ctx, files, mvToTerminate);
    }

    static async Task<IPEndPoint> ResolveAddressAsync(AddressFamily addressFamily, ProtocolType protocolType, string host, string port)
    {
        var addresses = await Dns.GetHostAddressesAsync(host);
        foreach (var address in addresses)
        {
            if (address.AddressFamily == addressFamily)
            {
                return new IPEndPoint(address, int.Parse(port));
            }
        }
        throw new Exception($"Failed to resolve address for {host}");
    }

    static async Task ScheduleThreads(string directory, Options opts, TMUContext ctx, Dictionary<string, List<string>> files, ManualResetEvent mvToTerminate)
    {
        var runningThreadsV = new SemaphoreSlim(0, opts.opThreads);
        var throttleV = new SemaphoreSlim(0, 100);
        var sentV = 0;
        var periodUS = (int)Math.Round(1000000 / opts.opRate);

        // Generate permissions to send at the correct rate
        Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(periodUS);
                throttleV.Release();
            }
        });

        // Report function
        Action<int> report = null;
        report = (s0) =>
        {
            var s = Interlocked.CompareExchange(ref sentV, 0, 0);
            if (s > s0)
            {
                Console.Write($"\r{new string(' ', 10 - s.ToString().Length)}{s} logs sent");
                Console.Out.Flush();
                report(s);
            }
        };
        Task.Run(() => report(0));

        foreach (var kvp in files)
        {
            if (mvToTerminate.WaitOne(0))
                break;

            if (runningThreadsV.CurrentCount >= opts.opThreads)
            {
                await runningThreadsV.WaitAsync();
            }

            var tmu = kvp.Key;
            var filesList = kvp.Value;
            _ = Task.Run(async () =>
            {
                await Send(directory, opts, ctx, tmu, filesList, throttleV, sentV);
                runningThreadsV.Release();
            });
        }

        // Wait for all threads to terminate
        while (runningThreadsV.CurrentCount > 0)
        {
            Console.Write($"\rwaiting for {runningThreadsV.CurrentCount} thread{(runningThreadsV.CurrentCount == 1 ? "" : "s")}...");
            Console.Out.Flush();
            await Task.Delay(100);
        }

        Console.WriteLine("\nfinished");
    }

    static async Task Send(string directory, Options opts, TMUContext ctx, string tmu, List<string> filesList, SemaphoreSlim throttleV, int sentV)
    {
        // Implement the logic to send files
        throw new NotImplementedException();
    }
}
