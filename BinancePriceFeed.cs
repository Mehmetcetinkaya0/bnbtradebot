using System;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace bnbtradebot
{
    /// <summary>
    /// BNB/USDT bookTicker akışını dinler. (Testnet/Mainnet seçilebilir)
    /// 3 bağlantı deseni dener: root+SUBSCRIBE, direct, combined.
    /// Otomatik yeniden bağlanma ve durum olayı sağlar.
    /// </summary>
    public sealed class BinancePriceFeed
    {
        public enum FeedState { Stopped, Connecting, Connected, Subscribing, Subscribed, Receiving, Stale, Reconnecting, Error }

        public sealed class Ticker { public decimal Bid { get; init; } public decimal Ask { get; init; } }

        public sealed class FeedStatus
        {
            public FeedState State { get; init; }
            public bool IsConnected { get; init; }
            public bool IsReceiving { get; init; }
            public DateTime? LastMessageUtc { get; init; }
            public int ReconnectCount { get; init; }
            public string? Endpoint { get; init; }
            public string? Stream { get; init; }
            public string? Error { get; init; }
            public bool UseTestnet { get; init; }
        }

        public event Action<Ticker>? OnTicker;
        public event Action<FeedStatus>? OnStatus;

        private readonly bool _useTestnet;
        private readonly string _stream = "bnbusdt@bookTicker";

        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private Task? _runTask;

        private int _reconnectCount;
        private DateTime? _lastMsgUtc;
        private string? _endpointInUse;

        public BinancePriceFeed(bool useTestnet = true) => _useTestnet = useTestnet;

        public Task StartAsync()
        {
            if (_runTask != null) return Task.CompletedTask;
            _cts = new CancellationTokenSource();
            _runTask = Task.Run(() => RunLoopAsync(_cts.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            try { _cts?.Cancel(); } catch { }
            try { if (_runTask != null) await _runTask; } catch { }
            await CloseWsAsync();
            _runTask = null;
            _cts?.Dispose(); _cts = null;
            SetState(FeedState.Stopped, false, false, null);
        }

        // ================= core loop =================
        private async Task RunLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    SetState(FeedState.Connecting, false, false, null);

                    if (await TryRootSubscribeAsync(token) ||
                        await TryDirectStreamAsync(token) ||
                        await TryCombinedAsync(token))
                    {
                        await ReadLoopAsync(token); // veri akışına girildi
                        continue;
                    }

                    throw new Exception("Binance WS patterns bağlanamadı.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _reconnectCount++;
                    SetState(FeedState.Error, false, false, ex.Message);
                    try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 + _reconnectCount)), token); } catch { break; }
                }
                finally
                {
                    await CloseWsAsync();
                }
            }
        }

        // ---------- patterns ----------
        private async Task<bool> TryRootSubscribeAsync(CancellationToken token)
        {
            var url = new Uri(_useTestnet
                ? "wss://testnet.binance.vision/ws"
                : "wss://stream.binance.com:9443/ws");

            _ws = new ClientWebSocket();
            _endpointInUse = url.ToString();
            await _ws.ConnectAsync(url, token);
            SetState(FeedState.Connected, true, false, null);

            // SUBSCRIBE
            var sub = $"{{\"method\":\"SUBSCRIBE\",\"params\":[\"{_stream}\"],\"id\":1}}";
            var bytes = Encoding.UTF8.GetBytes(sub);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            SetState(FeedState.Subscribed, true, false, null);
            return true;
        }

        private async Task<bool> TryDirectStreamAsync(CancellationToken token)
        {
            var url = new Uri(_useTestnet
                ? $"wss://testnet.binance.vision/ws/{_stream}"
                : $"wss://stream.binance.com:9443/ws/{_stream}");

            try
            {
                _ws = new ClientWebSocket();
                _endpointInUse = url.ToString();
                await _ws.ConnectAsync(url, token);
                SetState(FeedState.Subscribed, true, false, null);
                return true;
            }
            catch
            {
                await CloseWsAsync();
                return false;
            }
        }

        private async Task<bool> TryCombinedAsync(CancellationToken token)
        {
            var url = new Uri(_useTestnet
                ? $"wss://testnet.binance.vision/stream?streams={_stream}"
                : $"wss://stream.binance.com:9443/stream?streams={_stream}");

            try
            {
                _ws = new ClientWebSocket();
                _endpointInUse = url.ToString();
                await _ws.ConnectAsync(url, token);
                SetState(FeedState.Subscribed, true, false, null);
                return true;
            }
            catch
            {
                await CloseWsAsync();
                return false;
            }
        }

        // ---------- reading ----------
        private async Task ReadLoopAsync(CancellationToken token)
        {
            if (_ws == null) return;

            var buf = new byte[8192];
            var seg = new ArraySegment<byte>(buf);

            // watchdog: 15 sn mesaj yoksa STALE
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    if (_lastMsgUtc.HasValue && DateTime.UtcNow - _lastMsgUtc.Value > TimeSpan.FromSeconds(15))
                        SetState(FeedState.Stale, true, false, null);
                    try { await Task.Delay(3000, token); } catch { break; }
                }
            }, token);

            while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult res;
                try
                {
                    do
                    {
                        res = await _ws.ReceiveAsync(seg, token);
                        if (res.MessageType == WebSocketMessageType.Close)
                            throw new Exception($"WS kapandı: {res.CloseStatus} {res.CloseStatusDescription}");
                        sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                    } while (!res.EndOfMessage);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _reconnectCount++;
                    SetState(FeedState.Error, false, false, ex.Message);
                    return; // üst döngü reconnect
                }

                try
                {
                    var json = sb.ToString();
                    ParseAndEmit(json);
                }
                catch (Exception ex)
                {
                    SetState(FeedState.Error, true, false, "Parse: " + ex.Message);
                }
            }
        }

        private void ParseAndEmit(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var payload = root.TryGetProperty("data", out var data) ? data : root;

            decimal ask = 0m, bid = 0m;

            if (payload.TryGetProperty("a", out var a))
                decimal.TryParse(a.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out ask);
            if (payload.TryGetProperty("b", out var b))
                decimal.TryParse(b.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out bid);

            if (ask == 0m && payload.TryGetProperty("askPrice", out var ap))
                decimal.TryParse(ap.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out ask);
            if (bid == 0m && payload.TryGetProperty("bidPrice", out var bp))
                decimal.TryParse(bp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out bid);

            if (ask > 0m || bid > 0m)
            {
                _lastMsgUtc = DateTime.UtcNow;
                SetState(FeedState.Receiving, true, true, null);
                OnTicker?.Invoke(new Ticker { Ask = ask, Bid = bid });
            }
        }

        private async Task CloseWsAsync()
        {
            try
            {
                if (_ws != null && _ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }
            finally
            {
                _ws?.Dispose(); _ws = null;
            }
        }

        private void SetState(FeedState state, bool connected, bool receiving, string? error)
        {
            var s = new FeedStatus
            {
                State = state,
                IsConnected = connected,
                IsReceiving = receiving,
                LastMessageUtc = _lastMsgUtc,
                ReconnectCount = _reconnectCount,
                Endpoint = _endpointInUse,
                Stream = _stream,
                Error = error,
                UseTestnet = _useTestnet
            };
            OnStatus?.Invoke(s);
        }
    }
}
