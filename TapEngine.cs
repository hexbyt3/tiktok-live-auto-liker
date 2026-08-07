using System.Globalization;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace TikTokLiveAutoLiker;

enum TapSpeed { Relaxed, Brisk, Rapid }

/// <summary>
/// How long each press is held and how long the two presses are separated by. Both stay far
/// under the 500ms double-click window at every speed, so the dblclick still fires.
/// </summary>
readonly record struct SpeedProfile(
    int MoveMin, int MoveMax, int DwellMin, int DwellMax, int GapMin, int GapMax, int GlideSteps)
{
    public static SpeedProfile For(TapSpeed speed) => speed switch
    {
        TapSpeed.Rapid => new SpeedProfile(0, 2, 4, 10, 12, 26, 2),
        TapSpeed.Brisk => new SpeedProfile(3, 12, 12, 28, 28, 58, 4),
        _ => new SpeedProfile(15, 60, 35, 85, 55, 130, 7)
    };
}

sealed class TapSettings
{
    public int IntervalMinMs = 250;
    public int IntervalMaxMs = 600;
    public TapSpeed Speed = TapSpeed.Brisk;
    public bool IdleBreaks = true;
    public int MaxTaps;

    public TapSettings Clone() => (TapSettings)MemberwiseClone();
}

enum TapState { Idle, Tapping, NoPlayer, Resting }

/// <summary>
/// Sends double-taps into the embedded page through the DevTools Protocol. These land as
/// trusted input inside the renderer, so nothing is done with the real cursor or focus and
/// the window can sit behind whatever you're working on.
/// </summary>
sealed class TapEngine
{
    readonly CoreWebView2 _web;
    TapSettings _s = new();
    CancellationTokenSource? _cts;

    public event Action<TapState, string>? StateChanged;
    public event Action<int>? TapCounted;
    public event Action? Finished;

    public TapEngine(CoreWebView2 web) => _web = web;

    public bool Running { get; private set; }

    public int Taps { get; private set; }

    public void Start(TapSettings settings)
    {
        if (Running) return;
        _s = settings.Clone();
        Taps = 0;
        Running = true;
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    /// <summary>
    /// Swaps in new settings mid-run. The next tap uses them, and any rest currently being
    /// waited out is cut short so a big interval change doesn't take a minute to show up.
    /// </summary>
    public void Apply(TapSettings settings)
    {
        _s = settings.Clone();
        if (Running) _restBreaker?.Cancel();
    }

    public void Stop() => _cts?.Cancel();

    CancellationTokenSource? _restBreaker;

    /// <summary>Rests, returning early if the settings changed underneath us.</summary>
    async Task RestAsync(int ms, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _restBreaker = linked;
        try
        {
            await Task.Delay(ms, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // settings changed; fall through to the next tap immediately
        }
        finally
        {
            _restBreaker = null;
        }
    }

    public async Task TapOnceAsync(TapSettings settings)
    {
        var rect = await PlayerRectAsync();
        if (rect is null)
        {
            Report(TapState.NoPlayer, "No LIVE player on the page yet");
            return;
        }
        await SendDoubleTapAsync(rect.Value, settings, CancellationToken.None);
    }

    async Task LoopAsync(CancellationToken ct)
    {
        int missing = 0;
        RectangleF? cached = null;
        long cachedAt = 0;
        double cadence = 1.0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Re-locating the player costs a script round trip, so only redo it every few
                // seconds; the layout doesn't move while a stream is playing.
                if (cached is null || Environment.TickCount64 - cachedAt > 3000)
                {
                    cached = await PlayerRectAsync();
                    cachedAt = Environment.TickCount64;
                }

                if (cached is not { } rect)
                {
                    missing++;
                    Report(TapState.NoPlayer, missing > 3
                        ? "Waiting for the LIVE player - is the stream loaded?"
                        : "Looking for the player...");
                    await Task.Delay(1500, ct);
                    continue;
                }

                missing = 0;
                Report(TapState.Tapping, "Tapping");
                await SendDoubleTapAsync(rect, _s, ct);

                Taps++;
                TapCounted?.Invoke(Taps);
                if (_s.MaxTaps > 0 && Taps >= _s.MaxTaps) break;

                // Nobody taps at a fixed average for an hour. Letting the cadence wander keeps
                // the gap distribution from being suspiciously flat over a long session.
                cadence = Math.Clamp(cadence + Rng.Gaussian(0, 0.06), 0.75, 1.35);
                int wait = Math.Clamp(
                    (int)Math.Round(Rng.Triangular(_s.IntervalMinMs, _s.IntervalMaxMs) * cadence),
                    _s.IntervalMinMs, _s.IntervalMaxMs);

                if (_s.IdleBreaks && Rng.Chance(0.04))
                {
                    wait += Rng.Int(1800, 6500);
                    Report(TapState.Resting, "Taking a short break");
                }
                await RestAsync(wait, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Report(TapState.Idle, "Stopped: " + ex.Message);
        }
        finally
        {
            Running = false;
            _cts?.Dispose();
            _cts = null;
            Report(TapState.Idle, "Idle");
            Finished?.Invoke();
        }
    }

    async Task SendDoubleTapAsync(RectangleF player, TapSettings s, CancellationToken ct)
    {
        var (x, y) = PickPoint(player);
        var p = SpeedProfile.For(s.Speed);

        await GlideToAsync(x, y, p, ct);
        await Delay(Rng.Int(p.MoveMin, p.MoveMax), ct);

        // clickCount 1 then 2 is what makes Blink synthesise the dblclick TikTok listens for.
        await PressAsync(x, y, 1, p, ct);
        await Delay(Rng.Triangular(p.GapMin, p.GapMax), CancellationToken.None);
        await PressAsync(x, y, 2, p, ct);

        // People mashing a like button overshoot now and then; the odd third tap is normal.
        if (Rng.Chance(0.07))
        {
            await Delay(Rng.Triangular(p.GapMin, p.GapMax), CancellationToken.None);
            await PressAsync(x, y, 3, p, ct);
        }
    }

    async Task PressAsync(double x, double y, int clickCount, SpeedProfile p, CancellationToken ct)
    {
        await DispatchAsync("mousePressed", x, y, "left", buttons: 1, clickCount: clickCount);
        await Delay(Rng.Int(p.DwellMin, p.DwellMax), ct);
        await DispatchAsync("mouseReleased", x, y, "left", buttons: 0, clickCount: clickCount);
    }

    /// <summary>
    /// Walks the pointer across to the new spot instead of teleporting, so anything watching
    /// pointer movement sees an eased, slightly wobbly path rather than a single jump.
    /// </summary>
    async Task GlideToAsync(double x, double y, SpeedProfile p, CancellationToken ct)
    {
        var (fx, fy) = _lastPoint ?? (x, y);
        double dist = Math.Sqrt((x - fx) * (x - fx) + (y - fy) * (y - fy));
        int steps = dist < 24 ? 1 : Math.Clamp((int)(dist / 90) + Rng.Int(1, 3), 1, p.GlideSteps);

        for (int i = 1; i < steps; i++)
        {
            double t = (double)i / steps;
            double ease = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            double px = fx + (x - fx) * ease + Rng.Gaussian(0, 1.4);
            double py = fy + (y - fy) * ease + Rng.Gaussian(0, 1.4);
            await DispatchAsync("mouseMoved", Math.Round(px, 1), Math.Round(py, 1), "none", 0, 0);
            await Delay(Rng.Int(p.MoveMin, p.MoveMax), ct);
        }

        await DispatchAsync("mouseMoved", x, y, "none", buttons: 0, clickCount: 0);
        _lastPoint = (x, y);
    }

    (double X, double Y)? _lastPoint;

    static (double X, double Y) PickPoint(RectangleF r)
    {
        // Stay in the middle of the player: the bottom strip holds the transport controls
        // and the right edge holds the next/previous stream arrows.
        float left = r.Left + r.Width * 0.25f;
        float right = r.Right - r.Width * 0.25f;
        float top = r.Top + r.Height * 0.20f;
        float bottom = r.Bottom - r.Height * 0.28f;

        double cx = (left + right) / 2, cy = (top + bottom) / 2;
        double x = Math.Clamp(Rng.Gaussian(cx, (right - left) / 5), left, right);
        double y = Math.Clamp(Rng.Gaussian(cy, (bottom - top) / 5), top, bottom);
        return (Math.Round(x, 1), Math.Round(y, 1));
    }

    Task DispatchAsync(string type, double x, double y, string button, int buttons, int clickCount)
    {
        var json = string.Create(CultureInfo.InvariantCulture,
            $$"""
            {"type":"{{type}}","x":{{x}},"y":{{y}},"button":"{{button}}","buttons":{{buttons}},"clickCount":{{clickCount}},"modifiers":0,"pointerType":"mouse"}
            """);
        return _web.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", json);
    }

    const string FindPlayerScript = """
        (function () {
          function ok(r) { return r && r.width > 120 && r.height > 120; }
          var v = document.querySelector('video');
          var r = v && v.getBoundingClientRect();
          if (!ok(r)) {
            var o = document.querySelector('div[class*="cursor-pointer"][class*="flex-1"]');
            r = o && o.getBoundingClientRect();
          }
          if (!ok(r)) return null;
          var vw = document.documentElement.clientWidth, vh = document.documentElement.clientHeight;
          var x = Math.max(0, r.left), y = Math.max(0, r.top);
          var w = Math.min(r.right, vw) - x, h = Math.min(r.bottom, vh) - y;
          if (w < 120 || h < 120) return null;
          return JSON.stringify({ x: x, y: y, w: w, h: h });
        })()
        """;

    async Task<RectangleF?> PlayerRectAsync()
    {
        try
        {
            var raw = await _web.ExecuteScriptAsync(FindPlayerScript);
            if (string.IsNullOrEmpty(raw) || raw == "null") return null;

            // ExecuteScriptAsync hands back the result JSON-encoded, so a string result
            // arrives as a quoted string that still needs unwrapping.
            var inner = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(inner)) return null;

            using var doc = JsonDocument.Parse(inner);
            var e = doc.RootElement;
            return new RectangleF(
                (float)e.GetProperty("x").GetDouble(),
                (float)e.GetProperty("y").GetDouble(),
                (float)e.GetProperty("w").GetDouble(),
                (float)e.GetProperty("h").GetDouble());
        }
        catch (Exception)
        {
            return null;
        }
    }

    static Task Delay(int ms, CancellationToken ct) => ms <= 0 ? Task.CompletedTask : Task.Delay(ms, ct);

    TapState _reported = TapState.Idle;
    string _reportedDetail = "";

    void Report(TapState state, string detail)
    {
        if (state == _reported && detail == _reportedDetail) return;
        _reported = state;
        _reportedDetail = detail;
        StateChanged?.Invoke(state, detail);
    }
}
