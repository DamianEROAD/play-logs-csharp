using System.IO;
using System.IO.Compression;

namespace play_logs
{
    internal class LogState
    {
        public LogState(List<string> fileNames)
        {
            if (fileNames is null || fileNames.Count == 0)
                throw new ArgumentNullException(nameof(fileNames));
            _fileNames = fileNames;
        }

        public class LogLine
        {
            public string FileName { get; set; } = string.Empty;
            public int StartLineNo { get; set; }
            public string Line { get; set; } = string.Empty;
        }

        private readonly List<string> _fileNames;
        private LinkedList<string> _lines = new();
        private int _currentLineNo = 1;
        private string _currentFileName = string.Empty;

        public LogLine? Pop(string directory)
        {
            if (_lines.Count == 0)
            {
                var lines = LoadNextFile(directory);
                while ((lines is null || lines.Count == 0) && _fileNames.Count > 0)
                    lines = LoadNextFile(directory);

                if (lines is null || lines.Count == 0)
                    return null;

                _currentLineNo = 1;
                _lines = lines;
            }

            string line = _lines.First();
            _lines.RemoveFirst();

            int i = _currentLineNo;
            ++_currentLineNo;

            return new() { FileName = _currentFileName, StartLineNo = i, Line = line };
        }

        private LinkedList<string>? LoadNextFile(string directory)
        {
            if (_fileNames.Count == 0)
                return null;

            if (!string.IsNullOrEmpty(_currentFileName))
                File.Move(Path.Combine(directory, _currentFileName), Path.Combine(directory, $"{_currentFileName}.done"));

            _currentFileName = _fileNames[0];
            _fileNames.RemoveAt(0);

            var path = Path.Combine(directory, _currentFileName);

            if (_currentFileName.ToLower().EndsWith(".gz"))
                return new(ReadAndDecompressAllLines(path));

            return new(File.ReadAllLines(path));
        }

        private static LinkedList<string> ReadAndDecompressAllLines(string path)
        {
            byte[] bytes;
            using (var file = File.OpenRead(path))
            {
                using var compressedBytes = new GZipStream(file, CompressionMode.Decompress);
                using var uncompressedBytes = new MemoryStream();
                compressedBytes.CopyTo(uncompressedBytes);
                bytes = uncompressedBytes.ToArray();
            }
            string text = System.Text.Encoding.UTF8.GetString(bytes);
            return new(text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None));
        }
    }
}
