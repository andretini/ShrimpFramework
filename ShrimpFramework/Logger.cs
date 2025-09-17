using System.Net;

namespace ShrimpFramework;

public static class Logger
{
    public static void Log(HttpListenerRequest request)
    {
        Console.WriteLine(request.RemoteEndPoint + " " + request.HttpMethod + " /" + request.Url!.AbsoluteUri);
    }
}