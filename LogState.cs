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
    }
}
