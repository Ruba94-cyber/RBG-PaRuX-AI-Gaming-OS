using Windows.Gaming.Input;

// Developer/diagnostic probe: lists the raw game controllers Windows sees and prints every change (button, axis, hat) for N seconds.
// Usage: PaRuX.PadProbe.exe [seconds=30] [vendorIdHex]   (read-only; opens no virtual device and changes nothing)
int seconds = args.Length > 0 && int.TryParse(args[0], out int s) ? Math.Clamp(s, 1, 600) : 30;
int? vendor = args.Length > 1 ? Convert.ToInt32(args[1], 16) : null;

// The WinRT controller list fills a moment after the first request in a console process: wait for it (up to 8 s).
var pads = new List<RawGameController>();
for (int wait = 0; wait < 40 && pads.Count == 0; wait++)
{
    pads = RawGameController.RawGameControllers.Where(c => vendor is null || c.HardwareVendorId == vendor).ToList();
    if (pads.Count == 0) Thread.Sleep(200);
}
Console.WriteLine($"raw controllers: {pads.Count}");
foreach (var c in pads)
    Console.WriteLine($"  \"{c.DisplayName}\" VID {c.HardwareVendorId:X4} PID {c.HardwareProductId:X4} buttons={c.ButtonCount} axes={c.AxisCount} switches={c.SwitchCount} id={c.NonRoamableId}");
if (pads.Count == 0) return 2;

var state = pads.ToDictionary(c => c, c => (b: new bool[c.ButtonCount], sw: new GameControllerSwitchPosition[c.SwitchCount], ax: new double[c.AxisCount]));
var last = pads.ToDictionary(c => c, c => (b: new bool[c.ButtonCount], sw: new GameControllerSwitchPosition[c.SwitchCount], ax: new double[c.AxisCount]));
bool first = true;
DateTime end = DateTime.UtcNow.AddSeconds(seconds);
var t0 = DateTime.UtcNow;
while (DateTime.UtcNow < end)
{
    foreach (var c in pads)
    {
        var (b, sw, ax) = state[c];
        try { c.GetCurrentReading(b, sw, ax); }
        catch (Exception ex) { Console.WriteLine($"[{(DateTime.UtcNow - t0).TotalSeconds:F2}s] read failed: {ex.GetType().Name}"); Thread.Sleep(500); continue; }
        var (lb, lsw, lax) = last[c];
        string tag = $"{c.HardwareVendorId:X4}:{c.HardwareProductId:X4}";
        if (first)
        {
            Console.WriteLine($"  idle {tag}: axes=[{string.Join(", ", ax.Select(a => a.ToString("F3")))}] hat={string.Join(",", sw)} pressed=[{string.Join(",", b.Select((v, i) => v ? i : -1).Where(i => i >= 0))}]");
            Array.Copy(b, lb, b.Length); Array.Copy(sw, lsw, sw.Length); Array.Copy(ax, lax, ax.Length);
            continue;
        }
        for (int i = 0; i < b.Length; i++)
            if (b[i] != lb[i]) Console.WriteLine($"[{(DateTime.UtcNow - t0).TotalSeconds:F2}s] {tag} button {i} {(b[i] ? "DOWN" : "up")}");
        for (int i = 0; i < sw.Length; i++)
            if (sw[i] != lsw[i]) Console.WriteLine($"[{(DateTime.UtcNow - t0).TotalSeconds:F2}s] {tag} hat {i} = {sw[i]}");
        for (int i = 0; i < ax.Length; i++)
            if (Math.Abs(ax[i] - lax[i]) >= 0.06) Console.WriteLine($"[{(DateTime.UtcNow - t0).TotalSeconds:F2}s] {tag} axis {i} = {ax[i]:F3}");
        Array.Copy(b, lb, b.Length); Array.Copy(sw, lsw, sw.Length); Array.Copy(ax, lax, ax.Length);
    }
    first = false;
    Thread.Sleep(15);
}
return 0;
