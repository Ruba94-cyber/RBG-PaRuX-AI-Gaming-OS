using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// Real app, real Core, isolated profile. What cannot be done for real on this PC is simulated at its edge: a webhook receiver and a
/// Steam server are small local HTTP listeners; nothing inside PaRuX is replaced. Each test says what is simulated.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class SimulatedExternalsUiTests
{
    private static string Text(AutomationElement e) { try { return e.Name ?? string.Empty; } catch { return string.Empty; } }

    private static void Press(UiTestApplication app, string id) =>
        new InstallProgressPage(app).Invoke(UiWait.ById(app.MainWindow, id));

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class Listener : IDisposable
    {
        private readonly HttpListener _http = new();
        public List<(string Path, string Query, Dictionary<string, string> Headers, string Body)> Requests { get; } = new();
        public string Answer { get; set; } = "{}";
        public int Port { get; } = FreePort();

        public Listener()
        {
            _http.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _http.Start();
            _ = Task.Run(async () =>
            {
                while (_http.IsListening)
                {
                    try
                    {
                        HttpListenerContext c = await _http.GetContextAsync();
                        string body = await new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEndAsync();
                        var headers = c.Request.Headers.AllKeys.Where(k => k != null).ToDictionary(k => k!, k => c.Request.Headers[k] ?? string.Empty);
                        lock (Requests) Requests.Add((c.Request.Url!.AbsolutePath, c.Request.Url.Query, headers, body));
                        byte[] bytes = Encoding.UTF8.GetBytes(Answer);
                        c.Response.StatusCode = 200;
                        c.Response.ContentType = "application/json";
                        await c.Response.OutputStream.WriteAsync(bytes);
                        c.Response.Close();
                    }
                    catch (Exception) { break; }
                }
            });
        }

        public void Dispose() { try { _http.Stop(); _http.Close(); } catch { } }
    }

    private static void ScrollTo(UiTestApplication app, string automationId)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < until)
        {
            if (app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) != null) return;
            var scroller = app.MainWindow.FindAllDescendants()
                .Where(e => { try { return e.Patterns.Scroll.IsSupported && e.Patterns.Scroll.Pattern.VerticallyScrollable.ValueOrDefault; } catch { return false; } })
                .OrderByDescending(e => { try { return e.BoundingRectangle.Height; } catch { return 0; } }).FirstOrDefault();
            try { scroller?.Patterns.Scroll.Pattern.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement); } catch (System.Runtime.InteropServices.COMException) { }
            Thread.Sleep(200);
        }
        throw new TimeoutException("Timed out scrolling to " + automationId + ".");
    }

    private static AutomationElement Reveal(AutomationElement e)
    {
        try { e.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); } catch { }
        Thread.Sleep(250);
        return e;
    }

    [Fact, Trait("Category", "Regression"), Trait("Category", "FlaUI")]
    public void A_webhook_test_reaches_a_local_receiver_signed_with_the_secret_shown_once()
    {
        // Simulated: the receiver (a loopback HTTP listener). Real: Settings UI, Core, the delivery and the HMAC.
        const string name = nameof(A_webhook_test_reaches_a_local_receiver_signed_with_the_secret_shown_once);
        UiTestApplication.WaitForCorePortReleased();
        using var receiver = new Listener();
        using var app = new UiTestApplication(name);
        try
        {
            var main = new MainWindowPage(app.MainWindow);
            main.OpenSettings();
            main.OpenSettingsCategory("Settings_Category_PrivacyNetwork_Button");
            ScrollTo(app, "Settings_Webhooks_Add_Button");
            UiWait.ById(app.MainWindow, "Settings_Webhooks_Url_TextBox").AsTextBox().Text = $"http://127.0.0.1:{receiver.Port}/parux";
            UiWait.ById(app.MainWindow, "Settings_Webhooks_Events_TextBox").AsTextBox().Text = "webhook.";
            Reveal(UiWait.ById(app.MainWindow, "Settings_Webhooks_Add_Button")).AsButton().Invoke();

            string secret = string.Empty;
            UiWait.For(() =>
            {
                foreach (AutomationElement e in app.MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)))
                {
                    try { string v = e.AsTextBox().Text; if (v.StartsWith("whsec_", StringComparison.Ordinal)) { secret = v; return true; } } catch { }
                }
                return false;
            }, "the signing secret is shown", TimeSpan.FromSeconds(60));
            app.MainWindow.FindAllDescendants().First(e => Text(e) == "I copied it").Click();

            UiWait.For(() => app.MainWindow.FindAllDescendants().Any(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("Settings_Webhooks_Test_", StringComparison.Ordinal)), "the hook is listed", TimeSpan.FromSeconds(30));
            var test = app.MainWindow.FindAllDescendants().First(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("Settings_Webhooks_Test_", StringComparison.Ordinal));
            Reveal(test).AsButton().Invoke();

            // A hook subscribed to "webhook." also receives its own creation event; the test event is the one asked for here.
            UiWait.For(() => { lock (receiver.Requests) return receiver.Requests.Any(r => r.Headers.GetValueOrDefault("X-PaRuX-Event") == "webhook.test"); },
                "the receiver got the test delivery", TimeSpan.FromSeconds(30));
            (string path, _, Dictionary<string, string> headers, string body) = receiver.Requests.First(r => r.Headers.GetValueOrDefault("X-PaRuX-Event") == "webhook.test");
            Assert.Equal("/parux", path);
            Assert.Equal("webhook.test", headers["X-PaRuX-Event"]);
            string expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            Assert.Equal(expected, headers["X-PaRuX-Signature"]);
            Assert.Contains("webhook.test", body);

            // The result is reported in plain words next to the hook.
            var status = UiWait.ById(app.MainWindow, "Settings_Webhooks_Status_Text");
            UiWait.For(() => Text(status).Contains("Delivered", StringComparison.OrdinalIgnoreCase), "the owner is told it was delivered", TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) { app.CaptureFailure(name, ex); throw; }
    }
}
