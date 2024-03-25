using NDesk.Options;
using System.Net;
using System.Net.Sockets;
using play_logs;

class Program
{
    static void Usage()
    {
        Console.WriteLine("Play IngeniServer logs as if they are new logs being sent by TMUs to an IngeniServer");
        Console.WriteLine();
        Console.WriteLine("Note: The identity certificates used are generated ones, which means that");
        Console.WriteLine("identities must be deleted if you want to use real units on the same");
        Console.WriteLine("IngeniServer. Otherwise, the IngeniServer will see the units as impostors.");
        Console.WriteLine();
        Console.WriteLine("Usage: play-logs-csharp <options> <directory>");
        Console.WriteLine("    directory is the directory containing log files which must contain '.log' within");
        Console.WriteLine("    the name and they must start with '<serial_no>-', e.g. 060115061200508-window.log.gz");
        Console.WriteLine("    Files may be gzipped or not\n");

        _optionDefinitions.WriteOptionDescriptions(Console.Out);

        Environment.Exit(1);
    }

    class Options
    {
        public int NumThreads { get; set; }
        public string HostName { get; set; } = string.Empty;
        public int PortBase { get; set; }
        public double NumLogsPerSecond { get; set; }
    }

    static OptionSet _optionDefinitions = [];

    static void Main(string[] args)
    {
        var options = new Options
        {
            NumThreads = 20,
            HostName = "localhost",
            PortBase = 1490,
            NumLogsPerSecond = 100
        };

        _optionDefinitions = new OptionSet
        {
            { "h|hostname",
                "Host name of IngeniServer, default is localhost",
                (string host) => options.HostName = host },
            { "p|port-base",
                "Base port number of IngeniServer, default is 1490",
                (int port) => options.PortBase = port },
            { "t|threads",
                "Number of threads, default is " + options.NumThreads.ToString(),
                (int threads) => options.NumThreads = threads },
            { "r|rate",
                "Number of logs to send per second - overall, not per thread, default is " + options.NumLogsPerSecond,
                (double rate) => options.NumLogsPerSecond = rate }
        };

        List<string> otherOptions;
        string? directoryName = null;

        try
        {
            var extraArgs = _optionDefinitions.Parse(args);
            otherOptions = extraArgs.Take(extraArgs.Count - 1).ToList();
            if (extraArgs.Count > 0)
                directoryName = extraArgs.Last();
        }
        catch (OptionException e)
        {
            Console.WriteLine($"Error: {e.Message}");
            Usage();
            return;
        }

        if (directoryName == null)
        {
            Console.WriteLine("Missing required argument(s).");
            Usage();
            return;
        }

        try
        {
            var filesList = Directory.GetFiles(directoryName, "*.log")
                                     .Where(fileName => !fileName.EndsWith(".done"))
                                     .ToList();
            if (filesList.Count == 0)
                Console.WriteLine($"There are no *.log files within the directory {directoryName}");
            else
                PlayLogsAsync(directoryName, options, BuildLogContainer(filesList)).Wait();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error: {e.Message}");
            return;
        }
    }

    static Dictionary<string, List<string>> BuildLogContainer(List<string> fileNames)
    {
        var logFilesContainer = new Dictionary<string, List<string>>();
        foreach (var fileName in fileNames)
        {
            var serialNo = SerialNoOfFilename(Path.GetFileName(fileName));
            if (serialNo != null)
            {
                if (!logFilesContainer.ContainsKey(serialNo))
                    logFilesContainer[serialNo] = [];
                logFilesContainer[serialNo].Add(fileName);
            }
            else
                Console.WriteLine($"WARNING: can't extract the serial number from filename: {fileName}");
        }
        return logFilesContainer;
    }

    static string? SerialNoOfFilename(string fileName)
    {
        var serial = string.Concat(fileName.TakeWhile(char.IsDigit));
        if (serial.Length == 0 || !fileName[serial.Length..].StartsWith('-'))
            return null;
        return serial;
    }

    static async Task PlayLogsAsync(string directoryName, Options options, Dictionary<string, List<string>> logFilesContainer)
    {
        var serverStream = await ResolveAddressAsync(AddressFamily.Unspecified, ProtocolType.Tcp, options.HostName, (options.PortBase + 3).ToString());
        var serverDatagram = await ResolveAddressAsync(AddressFamily.InterNetwork, ProtocolType.Udp, options.HostName, (options.PortBase + 3).ToString());

        var tmuContext = new TMUContext(serverStream, serverDatagram, "simulated-tmu-dict.xml", false);

        ManualResetEvent exitApplication = new(false);
        _ = Task.Run(() =>
        {
            Console.WriteLine("press ENTER to terminate cooperatively");
            Console.ReadLine(); // <-- blocks the main thread here
            exitApplication.Set();
        });

        ScheduleThreads(directoryName, options, tmuContext, logFilesContainer, exitApplication);
    }

    static async Task<IPEndPoint> ResolveAddressAsync(AddressFamily addressFamily, ProtocolType protocolType, string host, string port)
    {
        if (host == "localhost")
            return new IPEndPoint(IPAddress.Parse("127.0.0.1"), int.Parse(port));

        var addresses = await Dns.GetHostAddressesAsync(host);
        foreach (var address in addresses)
        {
            if (address.AddressFamily == addressFamily)
                return new IPEndPoint(address, int.Parse(port));
        }

        throw new Exception($"Failed to resolve address for {host}");
    }

    private static readonly SemaphoreSlim _sendTickets = new(0, 100);

    static void StartSendThrottle(double numLogsPerSecond)
    {
        _ = Task.Run(async () =>
        {
            // Generate permissions to send at the correct rate
            var periodUS = (int)Math.Round(1000000 / numLogsPerSecond);
            while (true)
            {
                await Task.Delay(periodUS);
                _sendTickets.Release();
            }
        });
    }

    static readonly object _numLogsSentLock = new();
    static int _numLogsSent = 0;

    static void StartPrintingNumLogsSent()
    {
        _ = Task.Run(() =>
        {
            while (true)
            {
                lock (_numLogsSentLock)
                {
                    while (_numLogsSent == 0)
                        Monitor.Wait(_numLogsSentLock);
                    Console.Write($"\r{new string(' ', 10 - _numLogsSent.ToString().Length)}{_numLogsSent} logs sent");
                    Console.Out.Flush();
                }
            }
        });
    }

    static void UpdateNumLogsSent(int numLogsSent)
    {
        lock (_numLogsSentLock)
        {
            _numLogsSent = numLogsSent;
            Monitor.Pulse(_numLogsSentLock); // Notify waiting thread
        }
    }

    static void ScheduleThreads(string directory, Options options, TMUContext tmuContext, Dictionary<string, List<string>> logFilesContainer, ManualResetEvent exitApplication)
    {
        StartSendThrottle(options.NumLogsPerSecond);

        StartPrintingNumLogsSent();

        var threadLimit = StartSendingLogs(directory, options, tmuContext, logFilesContainer, exitApplication);

        WaitForAllThreads(threadLimit, options);
    }

    static SemaphoreSlim StartSendingLogs(string directory, Options options, TMUContext tmuContext, Dictionary<string, List<string>> logFilesContainer, ManualResetEvent exitApplication)
    {
        var threadLimit = new SemaphoreSlim(options.NumThreads);

        foreach (var logFileNames in logFilesContainer)
        {
            if (exitApplication.WaitOne(0))
                break;

            threadLimit.Wait();

            var serialNo = logFileNames.Key;
            var fileNames = logFileNames.Value;

            var t = new Thread(() => { SendAllLogs(threadLimit, directory, options, tmuContext, serialNo, fileNames); });
            t.Start();
        }

        return threadLimit;
    }

    static void WaitForAllThreads(SemaphoreSlim threadLimit, Options options)
    {
        while (threadLimit.CurrentCount < options.NumThreads)
        {
            int count = options.NumThreads - threadLimit.CurrentCount;
            Console.Write($"\rwaiting for {count} thread{(count == 1 ? "" : "s")}...");
            Console.Out.Flush();
            Thread.Sleep(100);
        }

        Console.WriteLine("\nfinished");
    }

    private static void SendAllLogs(SemaphoreSlim threadLimit, string directory, Options options, TMUContext tmuContext, string serialNo, List<string> logFileNames)
    {
        try
        {
            SendAllLogLines(directory, options, tmuContext, serialNo, logFileNames).Wait();
        }
        finally
        {
            threadLimit.Release();
        }
    }

    private static readonly Random _random = new(); // temp

    private enum WhatToDo
    {
        SendOne,
        AwaitOutstanding
    }

    static async Task SendAllLogLines(string directory, Options options, TMUContext tmuContext, string serialNo, List<string> logFileNames)
    {
        //var ident = GetIdentity(tmuContext.timers, serialNo);

        LogState logState = new(logFileNames);
        LogState.LogLine? logLine = logState.Pop();
        while (logLine != null)
        {
            //var channel = tmuComp(tmuContext, ident);
            //channel.Run();

            //var element = new Element() { Name = "log", Type = new Tuple<string, string>("type", "logger"), Contents = "log" };

            //await requestP(30000000, element);

            await Task.Delay(_random.Next(3500, 8000));

            logLine = logState.Pop();
        }
    }

    //static async Task<TMUIdent> GetIdentity(Timers timers, string serialNo)
    //{
    //    var dir = "play-logs.identities";
    //    if (!Directory.Exists(dir))
    //        Directory.CreateDirectory(dir);

    //    var context = await GetClientSSLCTX(timers, dir, serialNo);
    //    return new TMUIdent(context, serialNo, "serial no " + serialNo);
    //}

    //static async Task<object> GetClientSSLCTX(Timers timers, string dir, string serialNo)
    //{
    //    // Implement the logic to get the client SSL context here
    //    return null;
    //}

    //async Task Loop((string, int, byte[], Action)? mL, IEnumerable<Task> outstanding0)
    //{
    //    if (mL != null)
    //    {
    //        var (file, lineNo, l, commit) = mL.Value;
    //        try
    //        {
    //            var xml = Parse(defaultParseOptions, l);
    //            if (xml == null)
    //            {
    //                // Handle null XML
    //            }
    //            else if (xml.Name.LocalName == "sent")
    //            {
    //                var nextML = await Pop(); // Assuming Pop is a method that returns the next log
    //                await Loop(nextML, outstanding0);
    //            }
    //            else
    //            {
    //                var outstanding = await Block(outstanding0);
    //                var awaitReply = SendLog(xml);
    //                var process = awaitReply.ContinueWith(_ => commit.Invoke());
    //                var nextML = await Pop();
    //                await Loop(nextML, outstanding.Concat(new[] { process }));
    //            }
    //        }
    //        catch (Exception ex)
    //        {
    //            Console.Error.WriteLine($"{file}:{lineNo} {Encoding.UTF8.GetString(l)}");
    //            Console.Error.WriteLine($"    {ex.Message}");
    //            var nextML = await Pop();
    //            await Loop(nextML, outstanding0);
    //        }
    //    }
    //    else
    //    {
    //        foreach (var process in outstanding0)
    //        {
    //            await process;
    //            sentV.Value++;
    //        }
    //    }
    //}

    //static async Task<(string, int, byte[], Action)> PopLog(string directory, LogState stateV)
    //{
    //    while (true)
    //    {
    //        var ml = ReadLine(() => { }); // Assume ReadLine is a method that reads a line
    //        if (ml.Item1 != null)
    //        {
    //            return ml.Item1;
    //        }
    //        var result = ml.Item2;
    //        if (result.Item1 != null)
    //        {
    //            var file = result.Item1;
    //            var startLineNo = result.Item2;
    //            var commits = result.Item3;

    //            byte[] lines;
    //            using (var fileStream = File.OpenRead(Path.Combine(directory, file)))
    //            {
    //                var uncompressedStream = file.EndsWith(".gz") ? new GZipStream(fileStream, CompressionMode.Decompress) : fileStream;
    //                var reader = new StreamReader(uncompressedStream);
    //                var content = await reader.ReadToEndAsync();
    //                lines = Encoding.UTF8.GetBytes(content);
    //            }

    //            stateV.lsLines = new Tuple<string, int, byte[]>(file, lines.Skip(startLineNo - 1).ToArray(), startLineNo);
    //            commits();
    //        }
    //        else
    //        {
    //            commits();
    //            return null;
    //        }
    //    }
    //}

    //static (Tuple<string, int, byte[]>, Tuple<(string, int, Action)>) ReadLine(Action commits)
    //{
    //    // Implement the logic to read a line
    //    throw new NotImplementedException();
    //}
}
