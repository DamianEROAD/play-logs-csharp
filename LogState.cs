namespace play_logs
{
    internal class LogState
    {
        public List<string> lsFiles { get; set; } = [];
        public (string, List<byte>, int)? lsLines { get; set; }
    }
}
