using NDesk.Options;
using play_logs;
using System.Net;
using System.Net.Sockets;
using System.Xml;

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
    static readonly ManualResetEvent _allThreadsCompleted = new(false);
    static readonly ManualResetEvent _enterPressed = new(false);
    static readonly ManualResetEvent _throttlingThreadEnded = new(false);
    static readonly ManualResetEvent _printingThreadEnded = new(false);

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
            Console.CursorVisible = false;

            if (!Directory.Exists(directoryName))
                Directory.CreateDirectory(directoryName);

            var filesList = Directory.GetFiles(directoryName).Where(IsLogFileName).ToList();
            if (filesList.Count == 0)
                Console.WriteLine($"There are no *.log files within the directory {directoryName}");
            else
                PlayLogsThenWait(directoryName, options, BuildLogFilesList(filesList));
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error: {e.Message}");
        }
        finally
        {
            Console.CursorVisible = true;
        }
    }

    static bool IsLogFileName(string fileName)
    {
        return fileName.Contains(".log", StringComparison.InvariantCultureIgnoreCase) &&
              !fileName.EndsWith(".done", StringComparison.InvariantCultureIgnoreCase);
    }

    static Dictionary<string, List<string>> BuildLogFilesList(List<string> fileNames)
    {
        var logFilesList = new Dictionary<string, List<string>>();
        foreach (var fileName in fileNames)
        {
            var serialNo = SerialNoOfFilename(Path.GetFileName(fileName));
            if (serialNo != null)
            {
                if (!logFilesList.ContainsKey(serialNo))
                    logFilesList[serialNo] = [];
                logFilesList[serialNo].Add(fileName);
            }
            else
                Console.WriteLine($"WARNING: can't extract the serial number from filename: {fileName}");
        }
        return logFilesList;
    }

    static string? SerialNoOfFilename(string fileName)
    {
        var serial = string.Concat(fileName.TakeWhile(char.IsDigit));
        if (serial.Length == 0 || !fileName[serial.Length..].StartsWith('-'))
            return null;
        return serial;
    }

    static void PlayLogsThenWait(string directoryName, Options options, Dictionary<string, List<string>> logFilesList)
    {
        var serverStream = ResolveAddress(AddressFamily.Unspecified, ProtocolType.Tcp, options.HostName, (options.PortBase + 3).ToString());
        Console.WriteLine($"Remote TCP address is {serverStream}");

        var serverDatagram = ResolveAddress(AddressFamily.InterNetwork, ProtocolType.Udp, options.HostName, (options.PortBase + 3).ToString());
        Console.WriteLine($"Remote UDP address is {serverDatagram}");

        var tmuContext = new TMUContext(serverStream, serverDatagram, "simulated-tmu-dict.xml", false);

        var t = new Thread(() =>
        {
            Console.WriteLine("Press ENTER to terminate cooperatively");

            Console.ReadLine();
            _enterPressed.Set();

            if (_allThreadsCompleted.WaitOne(0))
                Console.WriteLine("Waiting for housekeeping threads to end...");
        });
        t.Start();

        ScheduleThreadsThenWait(directoryName, options, tmuContext, logFilesList);
        WaitForHouseKeepingThreadsToEnd();
    }

    static void WaitForHouseKeepingThreadsToEnd()
    {
        if (_enterPressed.WaitOne(0))
            Console.WriteLine("Waiting for housekeeping threads to end...");

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
            var periodUS = (int)Math.Round(1000000 / Math.Max(1, numLogsPerSecond)); // prevent DBZ
            while (!_allThreadsCompleted.WaitOne(0))
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
    static readonly object _printerCursorRowLock = new();

    static bool _printingKicked = false;
    static int _numLogsSent = 0;
    static int _totalLogFiles = 0;
    static int _printerCursorRow = 0;

    static void StartPrintingNumLogsSent()
    {
        var t = new Thread(() =>
        {
            lock (_printerCursorRowLock)
            {
                _printerCursorRow = Console.CursorTop;
            }

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

                if (_allThreadsCompleted.WaitOne(0))
                    break;

                string numSentString = $"{new string(' ', 10 - previousValue.ToString().Length)}{previousValue}";
                int percentCompleted = previousValue * 100 / _totalLogFiles;

                lock (_linePrinter)
                {
                    Console.CursorTop = _printerCursorRow;
                    Console.Write($"\r{numSentString}/{_totalLogFiles} logs sent ({percentCompleted}%)");
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

    static void CountLogFiles(Dictionary<string, List<string>> logFilesList)
    {
        _totalLogFiles = 0;
        foreach (var l in logFilesList)
            _totalLogFiles += l.Value.Count;
        KickPrintingThread();
    }

    static void ScheduleThreadsThenWait(string directory, Options options, TMUContext tmuContext, Dictionary<string, List<string>> logFilesList)
    {
        StartThrottlingTheSend(options.NumLogsPerSecond);

        StartPrintingNumLogsSent();

        CountLogFiles(logFilesList);

        var threadLimit = StartSendingLogs(directory, options, tmuContext, logFilesList);

        WaitForAllThreads(threadLimit, options);
    }

    static void WaitForAllThreads(SemaphoreSlim threadLimit, Options options)
    {
        int previousCount = 0;
        while (threadLimit.CurrentCount < options.NumThreads)
        {
            int count = options.NumThreads - threadLimit.CurrentCount;
            if (count != previousCount)
            {
                lock (_linePrinter)
                {
                    Console.CursorTop = _printerCursorRow + 1;
                    Console.Write($"\rWaiting for {count} thread{(count == 1 ? "" : "s")}...   ");
                    Console.Out.Flush();
                }
                previousCount = count;
            }
            Thread.Sleep(100);
        }

        lock (_linePrinter)
        {
            Console.CursorTop = _printerCursorRow + 2;
            Console.CursorVisible = true;
            if (_enterPressed.WaitOne(0))
                Console.WriteLine("\rFinished.");
            else
                Console.WriteLine("\rFinished. Press ENTER when you're ready...");
        }
        _allThreadsCompleted.Set();
    }

    static SemaphoreSlim StartSendingLogs(string directory, Options options, TMUContext tmuContext, Dictionary<string, List<string>> logFilesList)
    {
        var threadLimit = new SemaphoreSlim(options.NumThreads);

        foreach (var logFileNames in logFilesList)
        {
            if (_enterPressed.WaitOne(0))
                break;

            threadLimit.Wait();       // <-- notice we block here

            var serialNo = logFileNames.Key;
            var logs = logFileNames.Value;

            var t = new Thread(() => { SendLogsThread(threadLimit, directory, tmuContext, serialNo, logs); });
            t.Start();
        }

        return threadLimit;
    }

    private static void SendLogsThread(SemaphoreSlim threadLimit, string directory, TMUContext tmuContext, string serialNo, List<string> logFileNames)
    {
        try
        {
            SendLogs(directory, tmuContext, serialNo, logFileNames);
        }
        catch (Exception e)
        {
            Console.WriteLine($"(SendLogsThread) Error: {e.Message}");
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

    static void SendLogs(string directory, TMUContext tmuContext, string serialNo, List<string> logFileNames)
    {
        var ident = GetIdentity(/*tmuContext.timers,*/ serialNo);

        XmlParserContext context = new(null, null, null, XmlSpace.None);
        XmlReaderSettings settings = new() { ConformanceLevel = ConformanceLevel.Fragment };

        LogState logState = new(logFileNames);
        LogState.LogLine? logLine = logState.Pop(directory);
        while (logLine != null)
        {
            try
            {
                using XmlReader reader = XmlReader.Create(new StringReader(logLine.Line), settings, context);

                reader.MoveToContent();
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || string.Compare(reader.Name, "sent", true) == 0)
                        continue;

                    // string xmlElement0 = $"<log type=\"logger\">{log}</log>";
                    // string xmlElement1 = $"<request id=\"{allocID}\">{xmlElement0}</request>";

                    int channel = tmuComp(tmuContext, ident);
                    runChannel(channel);
                    IncrementNumLogsSent();
                }
            }
            catch (Exception ex)
            {
                lock (_linePrinter)
                {
                    Console.CursorVisible = false;
                    Console.CursorTop = _printerCursorRow + 4;
                    Console.WriteLine($"{logLine.FileName}({logLine.StartLineNo}): {logLine.Line}");
                    Console.CursorTop = _printerCursorRow + 5;
                    Console.WriteLine(ex.Message);
                }
            }

            logLine = logState.Pop(directory);
        }
    }

    static private int GetIdentity(/*tmuContext.timers,*/ string serialNo)
    {
        // TODO
        return 0;
    }

    static private int tmuComp(TMUContext tmuContext, int ident)
    {
        // TODO
        return 0;
    }

    static private void runChannel(int channel)
    {
        int channelState = 0;
        int conversationState = 0;

        bool completed = false;
        while (!completed)
        {
            var result = runConversation(channel, channelState, conversationState);
            switch (result)
            {
                case ConversationResult.Result:
                    completed = true;
                    break;
                case ConversationResult.Awaiting:
                    // TODO
                    break;
                case ConversationResult.Failure:
                    // TODO
                    break;
            }
        }
    }

    enum ConversationResult { Result, Awaiting, Failure }

    private static ConversationResult runConversation(int channel, int channelState, int conversationState)
    {
        // TODO
        Thread.Sleep(_random.Next(750, 1500)); // temp
        return ConversationResult.Result;
    }
}
