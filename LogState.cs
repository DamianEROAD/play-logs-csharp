namespace play_logs
{
    internal class LogState
    {
        public class LogLine
        {
            public string FileName { get; set; } = string.Empty;
            public List<byte> Bytes { get; set; } = [];
            public int StartLineNo { get; set; }
        }

        private readonly List<string> _fileNames;

        public LogState(List<string> fileNames)
        {
            _fileNames = fileNames;
        }

        public LogLine? Pop()
        {
            // TODO: this code is here just to kick the log sending loop
            if (_fileNames.Count == 0) return null;
            string s = _fileNames[0];
            _fileNames.RemoveAt(0);
            return new LogLine() { FileName = s };
        }

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
}
