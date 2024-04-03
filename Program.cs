using NDesk.Options;
using play_logs;
using System.Net;
using System.Net.Sockets;

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
    static ManualResetEvent _exitApplication = new(false);
    static ManualResetEvent _throttlingThreadEnded = new(false);
    static ManualResetEvent _printingThreadEnded = new(false);

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
            if (!Directory.Exists(directoryName))
                Directory.CreateDirectory(directoryName);

            var filesList = Directory.GetFiles(directoryName).Where(IsLogFileName).ToList();
            if (filesList.Count == 0)
                Console.WriteLine($"There are no *.log files within the directory {directoryName}");
            else
                PlayLogsThenWait(directoryName, options, BuildLogContainer(filesList));
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error: {e.Message}");
            return;
        }
    }

    static bool IsLogFileName(string fileName)
    {
        return fileName.Contains(".log", StringComparison.InvariantCultureIgnoreCase) &&
              !fileName.EndsWith(".done", StringComparison.InvariantCultureIgnoreCase);
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

    static void PlayLogsThenWait(string directoryName, Options options, Dictionary<string, List<string>> logFilesContainer)
    {
        var serverStream = ResolveAddress(AddressFamily.Unspecified, ProtocolType.Tcp, options.HostName, (options.PortBase + 3).ToString());
        Console.WriteLine($"Remote TCP address is {serverStream}");

        var serverDatagram = ResolveAddress(AddressFamily.InterNetwork, ProtocolType.Udp, options.HostName, (options.PortBase + 3).ToString());
        Console.WriteLine($"Remote UDP address is {serverDatagram}");

        var tmuContext = new TMUContext(serverStream, serverDatagram, "simulated-tmu-dict.xml", false);

        var t = new Thread(() =>
        {
            Console.WriteLine("press ENTER to terminate cooperatively");
            Console.ReadLine();
            _exitApplication.Set();
        });
        t.Start();

        ScheduleThreadsThenWait(directoryName, options, tmuContext, logFilesContainer);
        WaitForHouseKeepingThreadsToEnd();
    }

    static void WaitForHouseKeepingThreadsToEnd()
    {
        _exitApplication.Set();
        while (!(_throttlingThreadEnded.WaitOne(0) && _printingThreadEnded.WaitOne(0)))
        {
            KickPrintingThread();
            Thread.Sleep(100);
        }
    }

    static IPEndPoint ResolveAddress(AddressFamily addressFamily, ProtocolType protocolType, string host, string port)
    {
        // FIXME: use protocolType or remove it
        if (host == "localhost")
            return new IPEndPoint(IPAddress.Parse("127.0.0.1"), int.Parse(port));

        var addresses = Dns.GetHostAddresses(host);
        foreach (var address in addresses)
        {
            if (address.AddressFamily == addressFamily)
                return new IPEndPoint(address, int.Parse(port));
        }

        throw new Exception($"Failed to resolve address for {host}");
    }

    private static readonly SemaphoreSlim _sendTickets = new(0, 100);

    static void StartThrottlingTheSend(double numLogsPerSecond)
    {
        var t = new Thread(() =>
        {
            // Generate permissions to send at the correct rate
            var periodUS = (int)Math.Round(1000000 / numLogsPerSecond);
            while (!_exitApplication.WaitOne(0))
            {
                Thread.Sleep(periodUS);
                _sendTickets.Release();
            }

            _throttlingThreadEnded.Set();
        });
        t.Start();
    }

    static readonly object _linePrinter = new();

    static readonly object _numLogsSentLock = new();
    static bool _printingKicked = false;
    static int _numLogsSent = 0;
    static int _totalLogs = 0;

    static void StartPrintingNumLogsSent()
    {
        var t = new Thread(() =>
        {
            int previousValue = 0;
            while (true)
            {
                lock (_numLogsSentLock)
                {
                    while (_numLogsSent == previousValue && !_printingKicked)
                        Monitor.Wait(_numLogsSentLock);
                    previousValue = _numLogsSent;
                    _printingKicked = false;
                }

                if (_exitApplication.WaitOne(0))
                    break;

                string numSentString = $"{new string(' ', 10 - previousValue.ToString().Length)}{previousValue}";
                int percentCompleted = previousValue * 100 / _totalLogs;

                lock (_linePrinter)
                {
                    Console.Write($"\r{numSentString}/{_totalLogs} logs sent ({percentCompleted}%)");
                    Console.Out.Flush();
                }
            }

            _printingThreadEnded.Set();
        });
        t.Start();
    }

    static void KickPrintingThread()
    {
        lock (_numLogsSentLock)
        {
            _printingKicked = true;
            Monitor.Pulse(_numLogsSentLock); // Notify printing thread
        }
    }

    static void IncrementNumLogsSent(int increment = 1)
    {
        lock (_numLogsSentLock)
        {
            _numLogsSent += increment;
            Monitor.Pulse(_numLogsSentLock); // Notify printing thread
        }
    }

    static void CountLogFilesContainer(Dictionary<string, List<string>> logFilesContainer)
    {
        _totalLogs = 0;
        foreach (var l in logFilesContainer)
            _totalLogs += l.Value.Count;
        KickPrintingThread();
    }

    static void ScheduleThreadsThenWait(string directory, Options options, TMUContext tmuContext, Dictionary<string, List<string>> logFilesContainer)
    {
        StartThrottlingTheSend(options.NumLogsPerSecond);

        StartPrintingNumLogsSent();

        CountLogFilesContainer(logFilesContainer);

        var threadLimit = StartSendingLogs(directory, options, tmuContext, logFilesContainer);

        WaitForAllThreads(threadLimit, options);
    }

    static void WaitForAllThreads(SemaphoreSlim threadLimit, Options options)
    {
        while (threadLimit.CurrentCount < options.NumThreads)
        {
            int count = options.NumThreads - threadLimit.CurrentCount;
            lock (_linePrinter)
            {
                if (!_printingThreadEnded.WaitOne(0))
                    Console.CursorTop++;
                Console.Write($"\rWaiting for {count} thread{(count == 1 ? "" : "s")}...   ");
                if (!_printingThreadEnded.WaitOne(0))
                    Console.CursorTop--;
                Console.Out.Flush();
            }
            Thread.Sleep(100);
        }

        Console.CursorTop++;
        Console.WriteLine("\nFinished");
    }

    static SemaphoreSlim StartSendingLogs(string directory, Options options, TMUContext tmuContext, Dictionary<string, List<string>> logFilesContainer)
    {
        var threadLimit = new SemaphoreSlim(options.NumThreads);

        foreach (var logFileNames in logFilesContainer)
        {
            if (_exitApplication.WaitOne(0))
                break;

            threadLimit.Wait(); // Wait for the Send throttle to allow us to proceed

            var serialNo = logFileNames.Key;
            var fileNames = logFileNames.Value;

            var t = new Thread(() => { SendAllLogs(threadLimit, directory, options, tmuContext, serialNo, fileNames); });
            t.Start();
        }

        return threadLimit;
    }

    private static void SendAllLogs(SemaphoreSlim threadLimit, string directory, Options options, TMUContext tmuContext, string serialNo, List<string> logFileNames)
    {
        try
        {
            SendAllLogLines(directory, options, tmuContext, serialNo, logFileNames).Wait(); // block this thread here
        }
        finally
        {
            threadLimit.Release(); // strong guarantee
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

            await Task.Delay(_random.Next(1500, 3000)); // temp

            IncrementNumLogsSent();
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
}
