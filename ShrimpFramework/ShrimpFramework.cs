using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShrimpFramework;

using System.Net;
using System.Net.Sockets;
using System.Threading;

public static class HttpResult
{
    public static void Text(HttpListenerResponse res, string text, int status = 200, string contentType = "text/plain; charset=utf-8")
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        res.StatusCode = status;
        res.ContentType = contentType;
        res.ContentLength64 = bytes.Length;
        using var os = res.OutputStream;
        os.Write(bytes, 0, bytes.Length);
    }

    public static void Json(HttpListenerResponse res, object data, int status = 200)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        Text(res, json, status, "application/json; charset=utf-8");
    }
}

public class Route
{
    public string Method { get; }
    public string Pattern { get; }
    public Regex Regex { get; }
    public Func<HttpListenerRequest, HttpListenerResponse, RouteParams, Task> Handler { get; }

    public Route(string method, string pattern, Func<HttpListenerRequest, HttpListenerResponse, RouteParams, Task> handler)
    {
        Method = method.ToUpperInvariant();
        Pattern = pattern;
        Regex = RouteRegex(pattern);
        Handler = handler;
    }

    // Converts "/users/{id:int}" into a regex with named groups, e.g. ^/users/(?<id>\d+)$
    private static Regex RouteRegex(string pattern)
    {
        // Escape slashes etc., then replace {name:type?} with named groups
        // Supported types: int, guid, slug, * (greedy), default = [^/]+
        string rx = Regex.Replace(pattern, @"\{(?<name>[a-zA-Z_][a-zA-Z0-9_]*)?(:(?<type>int|guid|slug|\*))?\}", m =>
        {
            var name = m.Groups["name"].Value;
            var type = m.Groups["type"].Success ? m.Groups["type"].Value : "";
            string inner = type switch
            {
                "int"  => @"\d+",
                "str"  => @"[^/]+",
                "guid" => @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
                "slug" => @"[a-z0-9]+(?:-[a-z0-9]+)*",
                "*"    => @".+",
                _      => @"[^/]+"
            };

            return $@"(?<{name}>{inner})";
        });

        // Ensure leading ^ and trailing $
        if (!rx.StartsWith("^")) rx = "^" + rx;
        if (!rx.EndsWith("$"))   rx = rx + "$";

        return new Regex(rx, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }
}

public class RouteParams : Dictionary<string, string>
{
    public int GetInt(string key, int fallback = 0) => int.TryParse(this.GetValueOrDefault(key), out var v) ? v : fallback;
    public Guid GetGuid(string key) => Guid.TryParse(this.GetValueOrDefault(key), out var v) ? v : Guid.Empty;
}

public class Router
{
    
    private readonly List<Route> routes = new();
    public RouteGroup Group(string prefix) => new RouteGroup(this, prefix);

    public Router Get(string pattern, Func<HttpListenerRequest, HttpListenerResponse, RouteParams, Task> handler)
        => Add("GET", pattern, handler);

    public Router Post(string pattern, Func<HttpListenerRequest, HttpListenerResponse, RouteParams, Task> handler)
        => Add("POST", pattern, handler);

    public Router Put(string pattern, Func<HttpListenerRequest, HttpListenerResponse, RouteParams, Task> handler)
        => Add("PUT", pattern, handler);

    public Router Delete(string pattern, Func<HttpListenerRequest, HttpListenerResponse, RouteParams, Task> handler)
        => Add("DELETE", pattern, handler);

    public Router Add(string method, string pattern, Func<HttpListenerRequest, HttpListenerResponse, RouteParams, Task> handler)
    {
        routes.Add(new Route(method, pattern, handler));
        return this;
    }

    public async Task<bool> TryHandleAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        
        string path = req.Url!.AbsolutePath; // e.g. "/projects/"

        // Normalize: treat "/foo/" the same as "/foo"
        if (path.Length > 1 && path.EndsWith("/"))
            path = path.TrimEnd('/');

        var candidates = routes.Where(r => r.Regex.IsMatch(path)).ToList();
        
        if (candidates.Count == 0)
        {
            return false; // no route matched path, caller should send 404
        }

        // Match methods among those whose path matched
        var methodMatches = candidates.Where(r => r.Method == req.HttpMethod.ToUpperInvariant()).ToList();
        if (methodMatches.Count == 0)
        {
            // Path exists with different methods → 405 + Allow header
            var allow = string.Join(", ", candidates.Select(r => r.Method).Distinct());
            res.AddHeader("Allow", allow);
            HttpResult.Text(res, $"Method {req.HttpMethod} not allowed for {path}. Allowed: {allow}", 405);
            return true;
        }

        // Use the first matching route
        var route = methodMatches[0];
        var m = route.Regex.Match(path);
        var @params = new RouteParams();
        foreach (var gname in route.Regex.GetGroupNames())
        {
            if (int.TryParse(gname, out _)) continue; // skip numeric groups
            @params[gname] = m.Groups[gname].Value;
        }

        await route.Handler(req, res, @params);
        return true;
    }
}

public readonly struct RouteGroup
{
    private readonly Router _router;
    private readonly string _prefix; // normalized, no trailing '/'

    internal RouteGroup(Router router, string prefix)
    {
        _router = router;
        _prefix = (prefix ?? string.Empty).TrimEnd('/');
        if (_prefix.Length == 0) _prefix = ""; // allow root group
    }

    private string Combine(string pattern)
    {
        // Treat "/" or "" as the group's root => no trailing slash
        var tail = (pattern ?? "").Trim();
        if (tail.Length == 0 || tail == "/")
            return _prefix.Length == 0 ? "/" : _prefix;

        // Normal path join: ensure exactly one slash
        if (!tail.StartsWith("/")) tail = "/" + tail;
        return (_prefix.Length == 0 ? "" : _prefix) + tail;
    }

    public RouteGroup Get   (string pattern, Func<HttpListenerRequest,HttpListenerResponse,RouteParams,Task> h) { _router.Get   (Combine(pattern), h); return this; }
    public RouteGroup Post  (string pattern, Func<HttpListenerRequest,HttpListenerResponse,RouteParams,Task> h) { _router.Post  (Combine(pattern), h); return this; }
    public RouteGroup Put   (string pattern, Func<HttpListenerRequest,HttpListenerResponse,RouteParams,Task> h) { _router.Put   (Combine(pattern), h); return this; }
    public RouteGroup Delete(string pattern, Func<HttpListenerRequest,HttpListenerResponse,RouteParams,Task> h) { _router.Delete(Combine(pattern), h); return this; }
}


public class ShrimpServer
{
    private HttpListener listener;
    public Router router = new Router();

    private int port;
    private static int maxSimultaneousConnections = 20;
    
    private static readonly Semaphore sem = new Semaphore(maxSimultaneousConnections, maxSimultaneousConnections);

    public ShrimpServer(int port = 8080)
    {
        this.port = port;
    }

    // Register routes here (or expose Router to be configured by caller)
    private HttpListener InitializeListener(List<IPAddress> localhostIPs)
    {
        HttpListener listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");

        localhostIPs.ForEach(ip =>
        {
            Console.WriteLine($"Listening on IP http://{ip}:{port}/");
            listener.Prefixes.Add($"http://{ip}:{port}/");
        });

        return listener;
    }

    private void Start(HttpListener listener)
    {
        listener.Start();
        Task.Run(() => RunServer(listener));
    }

    public void Start()
    {
        List<IPAddress> localHostIPs = GetLocalHostIPs();
        listener = InitializeListener(localHostIPs);
        Start(listener);
    }

    private void RunServer(HttpListener listener)
    {
        while (true)
        {
            sem.WaitOne();
            StartConnectionListener(listener);
        }
    }

    private async void StartConnectionListener(HttpListener listener)
    {
        HttpListenerContext context = await listener.GetContextAsync();
        sem.Release();

        try
        {
            var req = context.Request;
            var res = context.Response;

            // Route dispatch
            bool handled = await router.TryHandleAsync(req, res);
            if (!handled)
            {
                // 404 Not Found
                HttpResult.Text(res, $"No route for {req.Url?.AbsolutePath}", 404);
            }
        }
        catch (Exception ex)
        {
            try
            {
                HttpResult.Text(context.Response, $"Internal Server Error: {ex.Message}", 500);
            }
            catch { /* ignore secondary failures */ }
        }
    }

    private List<IPAddress> GetLocalHostIPs()
    {
        IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
        return host.AddressList.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).ToList();
    }
}
