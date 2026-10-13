// Local stand-in for https://originsco.online/updates used to test the launcher, with fault
// injection the real site never does on purpose:
//   --root <dir>          folder published by UpdateBuilder (default C:\conquer\site-data\updates)
//   --port <n>            default 5099
//   --cut-every <n>       every n-th file download is cut after half of its bytes
//   --rate <bytes/s>      throttles every response (to close the launcher mid-download)
//   --corrupt <path.gz>   serves this file with flipped bytes once (tests the SHA-256 check)
//   --down                answers 503 to everything (tests offline play)
//   --latency <ms>        waits this long before every answer (a far-away player's round trip)

using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i < args.Length; i++)
    if (args[i].StartsWith("--"))
        options[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";

string root = options.GetValueOrDefault("root", @"C:\conquer\site-data\updates");
int port = int.Parse(options.GetValueOrDefault("port", "5099"));
int cutEvery = int.Parse(options.GetValueOrDefault("cut-every", "0"));
long rate = long.Parse(options.GetValueOrDefault("rate", "0"));
string? corrupt = options.GetValueOrDefault("corrupt");
bool down = options.ContainsKey("down");
int latency = int.Parse(options.GetValueOrDefault("latency", "0"));
int fileRequests = 0, corrupted = 0;

var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

app.Use(async (context, next) =>
{
    string path = context.Request.Path.Value ?? "";
    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {context.Request.Method} {path} range={context.Request.Headers.Range}");
    if (down)
    {
        context.Response.StatusCode = 503;
        return;
    }

    if (latency > 0) await Task.Delay(latency);
    bool isFile = path.StartsWith("/updates/files/", StringComparison.OrdinalIgnoreCase) ||
                  path.StartsWith("/updates/pack/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".bin");
    long limit = long.MaxValue;
    if (isFile && cutEvery > 0 && Interlocked.Increment(ref fileRequests) % cutEvery == 0)
        limit = -1; // decided once the length is known
    bool flip = isFile && corrupt != null && path.EndsWith(corrupt, StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Exchange(ref corrupted, 1) == 0;

    var original = context.Response.Body;
    await using var faulty = new FaultStream(original, context, limit, rate, flip);
    context.Response.Body = faulty;
    try
    {
        await next();
    }
    finally
    {
        context.Response.Body = original;
    }
});

app.MapGet("/api/news", () => new[]
{
    new {title = "Servidor de testes", date = "2026-10-13", text = "Notícias servidas pelo DevUpdateServer."},
    new {title = "Segunda notícia", date = "2026-10-10", text = "Texto mais longo para testar a quebra de linha no painel de notícias do launcher, que deve cortar com reticências quando passar do espaço disponível."}
});

var provider = new FileExtensionContentTypeProvider();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(root),
    RequestPath = "/updates",
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
    ContentTypeProvider = provider,
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});

Console.WriteLine($"serving {root} at http://127.0.0.1:{port}/updates/ cutEvery={cutEvery} rate={rate} latency={latency} corrupt={corrupt} down={down}");
app.Run();

/// <summary>Response body wrapper that throttles, cuts the connection half-way or flips bytes.</summary>
internal sealed class FaultStream(Stream inner, HttpContext context, long limit, long rate, bool flip) : Stream
{
    private long m_written;
    private readonly DateTime m_start = DateTime.UtcNow;

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (limit == -1)
            limit = Math.Max(1, (context.Response.ContentLength ?? 2) / 2);

        if (m_written + buffer.Length > limit)
        {
            int allowed = (int) Math.Max(0, limit - m_written);
            if (allowed > 0) await inner.WriteAsync(buffer[..allowed], ct);
            await inner.FlushAsync(ct);
            Console.WriteLine($"   cut {context.Request.Path} after {limit} bytes");
            context.Abort();
            throw new OperationCanceledException("cut by --cut-every");
        }

        if (flip)
        {
            var copy = buffer.ToArray();
            for (int i = 0; i < copy.Length; i += 97) copy[i] ^= 0xFF;
            buffer = copy;
        }

        await inner.WriteAsync(buffer, ct);
        m_written += buffer.Length;

        if (rate > 0)
        {
            var due = m_start + TimeSpan.FromSeconds((double) m_written / rate);
            var wait = due - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override void Flush() => inner.Flush();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => m_written; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
