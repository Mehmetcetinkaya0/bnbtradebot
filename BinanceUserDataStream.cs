using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace bnbtradebot
{
    /// <summary>
    /// Spot User Data Stream: listenKey + canlı cüzdan (balances) + emir olayları (executionReport).
    /// </summary>
    public sealed class BinanceUserDataStream : IDisposable
    {
        // MODELLER
        public sealed class Balance
        {
            public decimal Free { get; set; }
            public decimal Locked { get; set; }
            public decimal Total => Free + Locked;
        }

        public sealed class OrderUpdate
        {
            public long OrderId { get; set; }
            public string ClientOrderId { get; set; } = string.Empty;
            public string Symbol { get; set; } = string.Empty;
            public string Side { get; set; } = string.Empty;   // BUY/SELL
            public string Status { get; set; } = string.Empty; // NEW/PARTIALLY_FILLED/FILLED/CANCELED/...
            public string Type { get; set; } = string.Empty;   // LIMIT/MARKET
            public string TimeInForce { get; set; } = string.Empty;
            public decimal Price { get; set; }      // p
            public decimal OrigQty { get; set; }    // q
            public decimal ExecutedQty { get; set; }// z
            public decimal LastExecQty { get; set; }// l
            public decimal LastExecPrice { get; set; }// L
        }

        public enum UdsState { Stopped, CreatingListenKey, Connecting, Connected, Receiving, KeepAlive, Reconnecting, Error }

        public sealed class UdsStatus
        {
            public UdsState State { get; set; } = UdsState.Stopped;
            public string Message { get; set; } = string.Empty;
            public bool IsConnected { get; set; }
            public DateTime? LastMessageUtc { get; set; }
            public int Reconnects { get; set; }
        }

        // OLAYLAR
        public event Action<IReadOnlyDictionary<string, Balance>>? OnBalances;
        public event Action<UdsStatus>? OnStatus;
        public event Action<OrderUpdate>? OnOrderUpdate;

        // AYAR / ALTYAPI
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly bool _useTestnet;

        private readonly string _restBase;
        private readonly string _wsBase;

        private readonly HttpClient _http;
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private Task? _runner;

        private string? _listenKey;

        private readonly Dictionary<string, Balance> _balances = new(StringComparer.Ordinal);
        private readonly object _balLock = new();
        private readonly object _statusLock = new();
        private UdsStatus _status = new();

        // imza/saat
        private long _timeOffsetMs = 0;
        private DateTime _lastSyncUtc = DateTime.MinValue;

        public BinanceUserDataStream(string apiKey, string apiSecret, bool useTestnet = true, HttpMessageHandler? handler = null)
        {
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _apiSecret = apiSecret ?? throw new ArgumentNullException(nameof(apiSecret));
            _useTestnet = useTestnet;

            _restBase = useTestnet ? "https://testnet.binance.vision" : "https://api.binance.com";
            _wsBase = useTestnet ? "wss://stream.testnet.binance.vision/ws" : "wss://stream.binance.com:9443/ws";

            _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            _http.DefaultRequestHeaders.Add("X-MBX-APIKEY", _apiKey);
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        public void Dispose()
        {
            try { _http?.Dispose(); } catch { }
        }

        public Task StartAsync()
        {
            if (_runner is { IsCompleted: false }) return Task.CompletedTask;
            _cts = new CancellationTokenSource();
            _runner = Task.Run(() => RunAsync(_cts.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            try { _cts?.Cancel(); } catch { }

            var wsSnap = Interlocked.Exchange(ref _ws, null);
            if (wsSnap != null)
            {
                try { if (wsSnap.State == WebSocketState.Open) await wsSnap.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", CancellationToken.None); } catch { }
                wsSnap.Dispose();
            }

            if (!string.IsNullOrEmpty(_listenKey))
            {
                try { await CloseListenKeyAsync(_listenKey!); } catch { }
                _listenKey = null;
            }

            _cts?.Dispose(); _cts = null;
            _runner = null;
            SetStatus(UdsState.Stopped, "Stopped", false);
        }

        // ANA DÖNGÜ
        private async Task RunAsync(CancellationToken token)
        {
            int attempt = 0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    SetStatus(UdsState.CreatingListenKey, "Requesting listenKey…", false);
                    _listenKey = await CreateListenKeyAsync(token);

                    await EnsureTimeSyncAsync();
                    await PublishAccountSnapshotAsync(token);

                    var wsUrl = new Uri($"{_wsBase}/{_listenKey}");
                    var ws = new ClientWebSocket();
                    if (_useTestnet) ws.Options.SetRequestHeader("Origin", "https://stream.testnet.binance.vision");
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    Interlocked.Exchange(ref _ws, ws);

                    SetStatus(UdsState.Connecting, "Connecting WS…", false);
                    await ws.ConnectAsync(wsUrl, token);
                    SetStatus(UdsState.Connected, "Connected", true);

                    using var kaCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var keepAliveTask = Task.Run(() => KeepAliveLoopAsync(kaCts.Token), kaCts.Token);

                    var buf = new byte[8192];
                    var seg = new ArraySegment<byte>(buf);
                    var sb = new StringBuilder();

                    while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                    {
                        sb.Clear();
                        WebSocketReceiveResult res;
                        do
                        {
                            res = await ws.ReceiveAsync(seg, token);
                            if (res.MessageType == WebSocketMessageType.Close) break;
                            sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                        } while (!res.EndOfMessage);

                        if (res.MessageType == WebSocketMessageType.Close) break;

                        HandleInbound(sb.ToString());
                    }

                    kaCts.Cancel();
                    SetStatus(UdsState.Reconnecting, "Socket closed; reconnecting…", false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { SetStatus(UdsState.Error, "UDS error: " + ex.Message, false); }
                finally
                {
                    var wsSnap = Interlocked.Exchange(ref _ws, null);
                    if (wsSnap != null)
                    {
                        try { if (wsSnap.State == WebSocketState.Open) await wsSnap.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", CancellationToken.None); } catch { }
                        wsSnap.Dispose();
                    }
                }

                attempt++;
                if (token.IsCancellationRequested) break;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * Math.Pow(1.7, attempt), 20)), token);
            }
        }

        // MESAJ İŞLEME
        private void HandleInbound(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;

                if (!r.TryGetProperty("e", out var eTypeEl)) return;
                var eType = eTypeEl.GetString();

                if (eType == "outboundAccountPosition")
                {
                    var arr = r.GetProperty("B");
                    lock (_balLock)
                    {
                        foreach (var b in arr.EnumerateArray())
                        {
                            var asset = b.GetProperty("a").GetString()!;
                            var free = decimal.Parse(b.GetProperty("f").GetString()!, CultureInfo.InvariantCulture);
                            var locked = decimal.Parse(b.GetProperty("l").GetString()!, CultureInfo.InvariantCulture);
                            _balances[asset] = new Balance { Free = free, Locked = locked };
                        }
                        OnBalances?.Invoke(new Dictionary<string, Balance>(_balances));
                    }
                    SetStatus(UdsState.Receiving, "Account position", true);
                }
                else if (eType == "balanceUpdate")
                {
                    var asset = r.GetProperty("a").GetString()!;
                    var delta = decimal.Parse(r.GetProperty("d").GetString()!, CultureInfo.InvariantCulture);
                    lock (_balLock)
                    {
                        if (!_balances.TryGetValue(asset, out var bal))
                            bal = _balances[asset] = new Balance();
                        bal.Free += delta;
                        OnBalances?.Invoke(new Dictionary<string, Balance>(_balances));
                    }
                    SetStatus(UdsState.Receiving, $"Balance delta {asset}", true);
                }
                else if (eType == "executionReport")
                {
                    var upd = new OrderUpdate
                    {
                        Symbol = r.GetProperty("s").GetString() ?? "",
                        Side = r.GetProperty("S").GetString() ?? "",
                        Status = r.GetProperty("X").GetString() ?? "",
                        Type = r.GetProperty("o").GetString() ?? "",
                        TimeInForce = r.TryGetProperty("f", out var fEl) ? fEl.GetString() ?? "" : "",
                        OrderId = r.GetProperty("i").GetInt64(),
                        ClientOrderId = r.TryGetProperty("c", out var cEl) ? cEl.GetString() ?? "" : "",
                        Price = TryDec(r, "p"),
                        OrigQty = TryDec(r, "q"),
                        ExecutedQty = TryDec(r, "z"),
                        LastExecQty = TryDec(r, "l"),
                        LastExecPrice = TryDec(r, "L")
                    };
                    OnOrderUpdate?.Invoke(upd);
                    SetStatus(UdsState.Receiving, "Order exec", true);
                }
            }
            catch { /* parse hatası yoksay */ }
        }

        private static decimal TryDec(JsonElement root, string prop)
        {
            return root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
                ? decimal.Parse(el.GetString() ?? "0", CultureInfo.InvariantCulture)
                : 0m;
        }

        // listenKey / keepalive / snapshot
        private async Task<string> CreateListenKeyAsync(CancellationToken token)
        {
            var resp = await _http.PostAsync($"{_restBase}/api/v3/userDataStream", content: null, token);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("listenKey").GetString()!;
        }

        private async Task KeepAliveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(30), token);
                    if (string.IsNullOrEmpty(_listenKey)) continue;

                    var url = $"{_restBase}/api/v3/userDataStream?listenKey={_listenKey}";
                    var resp = await _http.PutAsync(url, content: null, token);
                    if (resp.IsSuccessStatusCode)
                        SetStatus(UdsState.KeepAlive, "listenKey keep-alive OK", true);
                    else
                    {
                        SetStatus(UdsState.Error, "listenKey keep-alive failed", false);
                        break;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch { break; }
            }
        }

        private async Task CloseListenKeyAsync(string listenKey)
        {
            try { await _http.DeleteAsync($"{_restBase}/api/v3/userDataStream?listenKey={listenKey}"); } catch { }
        }

        private void SetStatus(UdsState state, string message, bool connected)
        {
            lock (_statusLock)
            {
                if (state == UdsState.Receiving) _status.LastMessageUtc = DateTime.UtcNow;
                if (state == UdsState.Reconnecting) _status.Reconnects++;
                _status.State = state;
                _status.Message = message;
                _status.IsConnected = connected;
            }
            OnStatus?.Invoke(GetStatus());
        }

        public UdsStatus GetStatus()
        {
            lock (_statusLock)
            {
                return new UdsStatus
                {
                    State = _status.State,
                    Message = _status.Message,
                    IsConnected = _status.IsConnected,
                    LastMessageUtc = _status.LastMessageUtc,
                    Reconnects = _status.Reconnects
                };
            }
        }

        private async Task PublishAccountSnapshotAsync(CancellationToken token)
        {
            var qs = $"recvWindow=5000&timestamp={NowMs()}";
            var url = $"{_restBase}/api/v3/account?{qs}&signature={Sign(qs)}";
            var resp = await _http.GetAsync(url, token);
            resp.EnsureSuccessStatusCode();
            var txt = await resp.Content.ReadAsStringAsync(token);

            using var doc = JsonDocument.Parse(txt);
            var arr = doc.RootElement.GetProperty("balances");

            lock (_balLock)
            {
                _balances.Clear();
                foreach (var b in arr.EnumerateArray())
                {
                    var asset = b.GetProperty("asset").GetString()!;
                    var free = decimal.Parse(b.GetProperty("free").GetString()!, CultureInfo.InvariantCulture);
                    var locked = decimal.Parse(b.GetProperty("locked").GetString()!, CultureInfo.InvariantCulture);
                    _balances[asset] = new Balance { Free = free, Locked = locked };
                }
                OnBalances?.Invoke(new Dictionary<string, Balance>(_balances));
            }
        }

        // imza/saat
        private async Task EnsureTimeSyncAsync()
        {
            if (_timeOffsetMs != 0 && (DateTime.UtcNow - _lastSyncUtc) < TimeSpan.FromMinutes(10)) return;

            var resp = await _http.GetAsync($"{_restBase}/api/v3/time");
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            long serverMs = doc.RootElement.GetProperty("serverTime").GetInt64();
            long localMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _timeOffsetMs = serverMs - localMs;
            _lastSyncUtc = DateTime.UtcNow;
        }

        private long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _timeOffsetMs;

        private string Sign(string query)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_apiSecret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(query));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
