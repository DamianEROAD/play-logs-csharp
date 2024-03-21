using System.Net;

internal class TMUContext
{
    private IPEndPoint serverStream;
    private IPEndPoint serverDatagram;
    private string v;
    private bool capture;

    public TMUContext(IPEndPoint serverStream, IPEndPoint serverDatagram, string v, bool capture)
    {
        this.serverStream = serverStream;
        this.serverDatagram = serverDatagram;
        this.v = v;
        this.capture = capture;
    }
}