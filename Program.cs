using NDesk.Options;
using play_logs;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

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

        try
        {
            var filesList = Directory.GetFiles(directory, "*.log")
                                     .Where(fn => !fn.EndsWith(".done"))
                                     .ToList();
            var opts = new EmptyOptions(); // Define the empty options class
            var files = OrganizeBySerialNo(filesList);
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
            return null;
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
        var ident = GetIdentity(ctx.Timers, tmu); // Assuming GetIdentity is a method that returns a value
        var stateV = new TVar<LogState>(new LogState(files, null)); // Assuming LogState constructor takes lsFiles and lsLines
        var sesRef = new IORef<object>(null); // Assuming IORef is similar to C#'s Ref and newIORef creates a new instance

        Func<Task<(string, int, byte[], Action)>> pop = async () => await PopLog(directory, stateV); // Assuming PopLog returns a tuple
        var mL0 = await pop();

        if (mL0 != null)
        {
            RunChannel(ctx, ident, sesRef, () =>
            {
                var sendLog = new Func<UNode<Text>, Task>(log =>
                {
                    // Implement the logic for sending log
                    throw new NotImplementedException();
                });

                var bufferSize = 30; // Maximum number of simultaneous requests to queue up

                Func<Seq<XMLParallel<XMLChannel<IO>, Unit>>, Task<Seq<XMLParallel<XMLChannel<IO>, Unit>>>> block = async (outstanding) =>
                {
                    var toDo = await Task.Run(() =>
                    {
                        var t = throttleV.Value;
                        if (t > 0)
                        {
                            throttleV.OnNext(t - 1);
                            return WhatToDo.SendOne;
                        }
                        else if (outstanding.Count > 0)
                        {
                            return WhatToDo.AwaitOutstanding;
                        }
                        else
                        {
                            throw new InvalidOperationException("Unexpected condition");
                        }
                    });

                    Seq<XMLParallel<XMLChannel<IO>, Unit>> outstanding1;
                    if (outstanding.Count > bufferSize || toDo == WhatToDo.AwaitOutstanding)
                    {
                        var process = outstanding[0];
                        await process;
                        sentV.OnNext(sentV.Value + 1);
                        outstanding1 = outstanding.Skip(1);
                    }
                    else
                    {
                        outstanding1 = outstanding;
                    }

                    return toDo == WhatToDo.SendOne ? outstanding1 : await block(outstanding1);
                };
            });
        }
    }

    async Task Loop((string, int, byte[], Action)? mL, IEnumerable<Task> outstanding0)
    {
        if (mL != null)
        {
            var (file, lineNo, l, commit) = mL.Value;
            try
            {
                var xml = Parse(defaultParseOptions, l);
                if (xml == null)
                {
                    // Handle null XML
                }
                else if (xml.Name.LocalName == "sent")
                {
                    var nextML = await Pop(); // Assuming Pop is a method that returns the next log
                    await Loop(nextML, outstanding0);
                }
                else
                {
                    var outstanding = await Block(outstanding0);
                    var awaitReply = SendLog(xml);
                    var process = awaitReply.ContinueWith(_ => commit.Invoke());
                    var nextML = await Pop();
                    await Loop(nextML, outstanding.Concat(new[] { process }));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{file}:{lineNo} {Encoding.UTF8.GetString(l)}");
                Console.Error.WriteLine($"    {ex.Message}");
                var nextML = await Pop();
                await Loop(nextML, outstanding0);
            }
        }
        else
        {
            foreach (var process in outstanding0)
            {
                await process;
                sentV.Value++;
            }
        }
    }

    static async Task<(string, int, byte[], Action)> PopLog(string directory, LogState stateV)
    {
        while (true)
        {
            var ml = ReadLine(() => { }); // Assume ReadLine is a method that reads a line
            if (ml.Item1 != null)
            {
                return ml.Item1;
            }
            var result = ml.Item2;
            if (result.Item1 != null)
            {
                var file = result.Item1;
                var startLineNo = result.Item2;
                var commits = result.Item3;

                byte[] lines;
                using (var fileStream = File.OpenRead(Path.Combine(directory, file)))
                {
                    var uncompressedStream = file.EndsWith(".gz") ? new GZipStream(fileStream, CompressionMode.Decompress) : fileStream;
                    var reader = new StreamReader(uncompressedStream);
                    var content = await reader.ReadToEndAsync();
                    lines = Encoding.UTF8.GetBytes(content);
                }

                stateV.lsLines = new Tuple<string, int, byte[]>(file, lines.Skip(startLineNo - 1).ToArray(), startLineNo);
                commits();
            }
            else
            {
                commits();
                return null;
            }
        }
    }

    enum WhatToDo
    {
        SendOne,
        AwaitOutstanding
    }

    static (Tuple<string, int, byte[]>, Tuple<(string, int, Action)>) ReadLine(Action commits)
    {
        // Implement the logic to read a line
        throw new NotImplementedException();
    }
}
