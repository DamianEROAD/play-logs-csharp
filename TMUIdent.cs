namespace play_logs
{
    internal class TMUIdent
    {
        public object sslCtx;
        public string serialNo;
        public string message;

        public TMUIdent(object sslCtx, string serialNo, string message)
        {
            this.sslCtx = sslCtx;
            this.serialNo = serialNo;
            this.message = message;
        }

        public override string ToString()
        {
            return $"TMUIdent: SSLContext={sslCtx}, SerialNo={serialNo}, Message={message}";
        }
    }
}
