using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace bnbtradebot
{
    /// <summary>
    /// Binance Spot REST (testnet/mainnet) – hesap, market verisi ve emir uçları (SIGNED).
    /// </summary>
    public sealed class BinanceRestClient : IDisposable
    {
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly string _baseUrl;
        private readonly HttpClient _http;

        private long _timeOffsetMs = 0;
        private DateTime _lastSyncUtc = DateTime.MinValue;

        public BinanceRestClient(string apiKey, string apiSecret, bool useTestnet = true, HttpMessageHandler? handler = null)
        {
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            _apiSecret = apiSecret ?? throw new ArgumentNullException(nameof(apiSecret));
            _baseUrl = useTestnet ? "https://testnet.binance.vision" : "https://api.binance.com";
            _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            _http.DefaultRequestHeaders.Add("X-MBX-APIKEY", _apiKey);
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        public void Dispose() => _http.Dispose();

        // ===== HESAP =====
        public async Task<List<BalanceLine>> GetBalancesAsync(bool hideZero = true)
        {
            await EnsureTimeSyncAsync();

            var qs = $"recvWindow=5000&timestamp={NowMs()}";
            var url = $"{_baseUrl}/api/v3/account?{qs}&signature={Sign(qs)}";

            var resp = await _http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode) ThrowApiError(resp.StatusCode, body);

            using var doc = JsonDocument.Parse(body);
            var balances = new List<BalanceLine>();

            foreach (JsonElement bal in doc.RootElement.GetProperty("balances").EnumerateArray())
            {
                string asset = bal.GetProperty("asset").GetString() ?? "";
                decimal free = decimal.Parse(bal.GetProperty("free").GetString() ?? "0", CultureInfo.InvariantCulture);
                decimal locked = decimal.Parse(bal.GetProperty("locked").GetString() ?? "0", CultureInfo.InvariantCulture);

                if (hideZero && free == 0m && locked == 0m) continue;
                balances.Add(new BalanceLine { Asset = asset, Free = free, Locked = locked });
            }
            return balances;
        }

        // ===== MARKET =====
        public async Task<ExchangeSymbol?> GetSymbolInfoAsync(string symbol)
        {
            var url = $"{_baseUrl}/api/v3/exchangeInfo?symbol={symbol}";
            var resp = await _http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(body);
            var arr = doc.RootElement.GetProperty("symbols");
            if (arr.GetArrayLength() == 0) return null;
            var s = arr[0];

            var info = new ExchangeSymbol
            {
                Symbol = s.GetProperty("symbol").GetString() ?? symbol,
                Status = s.GetProperty("status").GetString() ?? "TRADING",
                BaseAsset = s.GetProperty("baseAsset").GetString() ?? "",
                QuoteAsset = s.GetProperty("quoteAsset").GetString() ?? ""
            };

            foreach (var f in s.GetProperty("filters").EnumerateArray())
            {
                var type = f.GetProperty("filterType").GetString();

                if (type == "LOT_SIZE")
                {
                    var stepStr = f.GetProperty("stepSize").GetString() ?? "0";
                    info.StepSize = Dec(stepStr);
                    info.MinQty = Dec(f.GetProperty("minQty").GetString() ?? "0");
                    info.QuantityPrecision = PrecisionFromStepStr(stepStr);
                }
                else if (type == "PRICE_FILTER")
                {
                    var tickStr = f.GetProperty("tickSize").GetString() ?? "0";
                    info.TickSize = Dec(tickStr);
                    info.PricePrecision = PrecisionFromStepStr(tickStr);
                }
                else if (type == "MIN_NOTIONAL" || type == "NOTIONAL")
                {
                    info.MinNotional = Dec(f.GetProperty("minNotional").GetString() ?? "0");
                }
            }
            return info;
        }

        public async Task<decimal> GetAskPriceAsync(string symbol)
        {
            var url = $"{_baseUrl}/api/v3/ticker/bookTicker?symbol={symbol}";
            var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return Dec(doc.RootElement.GetProperty("askPrice").GetString() ?? "0");
        }

        // ===== EMİRLER =====

        public async Task<OrderResult> PlaceMarketSellAsync(string symbol, decimal quantity, string? clientId = null)
        {
            await EnsureTimeSyncAsync();

            string qStr = quantity.ToString(CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            sb.Append($"symbol={symbol}&side=SELL&type=MARKET&quantity={qStr}&recvWindow=5000&timestamp={NowMs()}");
            if (!string.IsNullOrEmpty(clientId)) sb.Append($"&newClientOrderId={clientId}");
            var qs = sb.ToString();

            var url = $"{_baseUrl}/api/v3/order?{qs}&signature={Sign(qs)}";
            var resp = await _http.PostAsync(url, content: null);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode) ThrowApiError(resp.StatusCode, body);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            return new OrderResult
            {
                Symbol = root.GetProperty("symbol").GetString() ?? symbol,
                Side = root.GetProperty("side").GetString() ?? "SELL",
                OrderId = root.GetProperty("orderId").GetInt64(),
                ClientOrderId = root.GetProperty("clientOrderId").GetString() ?? "",
                Status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                Price = Dec(root.TryGetProperty("price", out var pr) ? (pr.GetString() ?? "0") : "0"),
                OrigQty = Dec(root.TryGetProperty("origQty", out var oq) ? (oq.GetString() ?? "0") : "0"),
                ExecutedQty = Dec(root.TryGetProperty("executedQty", out var eq) ? (eq.GetString() ?? "0") : "0")
            };
        }

        public async Task<OrderResult> PlaceLimitOrderAsync(
            string symbol, string side, decimal price, decimal quantity,
            ExchangeSymbol symbolInfo, string tif = "GTC", string? clientId = null)
        {
            await EnsureTimeSyncAsync();

            if (symbolInfo is null) throw new ArgumentNullException(nameof(symbolInfo));
            if (!string.Equals(side, "BUY", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("side BUY veya SELL olmalı.");

            decimal adjPrice = side.Equals("BUY", StringComparison.OrdinalIgnoreCase)
                ? FloorToTick(price, symbolInfo.TickSize, symbolInfo.PricePrecision)
                : CeilToTick(price, symbolInfo.TickSize, symbolInfo.PricePrecision);

            decimal adjQty = FloorToStep(quantity, symbolInfo.StepSize, symbolInfo.QuantityPrecision);

            if (adjQty < symbolInfo.MinQty)
                throw new InvalidOperationException($"Miktar minQty altı: {adjQty} < {symbolInfo.MinQty}");

            if (adjPrice * adjQty < symbolInfo.MinNotional)
                throw new InvalidOperationException($"Notional yetersiz: {(adjPrice * adjQty):F2} < {symbolInfo.MinNotional:F2}");

            string pStr = adjPrice.ToString(CultureInfo.InvariantCulture);
            string qStr = adjQty.ToString(CultureInfo.InvariantCulture);

            var sb = new StringBuilder();
            sb.Append($"symbol={symbol}&side={side.ToUpperInvariant()}&type=LIMIT&timeInForce={tif}&quantity={qStr}&price={pStr}&recvWindow=5000&timestamp={NowMs()}");
            if (!string.IsNullOrEmpty(clientId)) sb.Append($"&newClientOrderId={clientId}");
            var qs = sb.ToString();

            var url = $"{_baseUrl}/api/v3/order?{qs}&signature={Sign(qs)}";
            var resp = await _http.PostAsync(url, content: null);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode) ThrowApiError(resp.StatusCode, body);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            return new OrderResult
            {
                Symbol = root.GetProperty("symbol").GetString() ?? symbol,
                Side = root.GetProperty("side").GetString() ?? side.ToUpperInvariant(),
                OrderId = root.GetProperty("orderId").GetInt64(),
                ClientOrderId = root.GetProperty("clientOrderId").GetString() ?? "",
                Status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                Price = adjPrice,
                OrigQty = adjQty,
                ExecutedQty = Dec(root.TryGetProperty("executedQty", out var eq) ? (eq.GetString() ?? "0") : "0")
            };
        }

        public async Task<List<OpenOrder>> GetOpenOrdersAsync(string? symbol = null)
        {
            await EnsureTimeSyncAsync();
            var qs = $"recvWindow=5000&timestamp={NowMs()}";
            if (!string.IsNullOrEmpty(symbol)) qs = $"symbol={symbol}&" + qs;

            var url = $"{_baseUrl}/api/v3/openOrders?{qs}&signature={Sign(qs)}";
            var resp = await _http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode) ThrowApiError(resp.StatusCode, body);

            using var doc = JsonDocument.Parse(body);
            var list = new List<OpenOrder>();
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                list.Add(new OpenOrder
                {
                    Symbol = o.GetProperty("symbol").GetString() ?? "",
                    Side = o.GetProperty("side").GetString() ?? "",
                    OrderId = o.GetProperty("orderId").GetInt64(),
                    ClientOrderId = o.GetProperty("clientOrderId").GetString() ?? "",
                    Price = Dec(o.GetProperty("price").GetString() ?? "0"),
                    OrigQty = Dec(o.GetProperty("origQty").GetString() ?? "0"),
                    ExecutedQty = Dec(o.GetProperty("executedQty").GetString() ?? "0"),
                    Status = o.GetProperty("status").GetString() ?? "NEW",
                    TimeInForce = o.GetProperty("timeInForce").GetString() ?? "GTC",
                    Type = o.GetProperty("type").GetString() ?? "LIMIT"
                });
            }
            return list;
        }

        public async Task CancelOrderAsync(string symbol, long orderId)
        {
            await EnsureTimeSyncAsync();
            var qs = $"symbol={symbol}&orderId={orderId}&recvWindow=5000&timestamp={NowMs()}";
            var url = $"{_baseUrl}/api/v3/order?{qs}&signature={Sign(qs)}";
            var resp = await _http.DeleteAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) ThrowApiError(resp.StatusCode, body);
        }

        public async Task CancelAllOpenOrdersAsync(string symbol)
        {
            await EnsureTimeSyncAsync();
            var qs = $"symbol={symbol}&recvWindow=5000&timestamp={NowMs()}";
            var url = $"{_baseUrl}/api/v3/openOrders?{qs}&signature={Sign(qs)}";
            var resp = await _http.DeleteAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) ThrowApiError(resp.StatusCode, body);
        }

        // ===== ARAÇLAR =====
        public static int PrecisionFromStepStr(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 8;
            s = s.TrimEnd('0').TrimEnd('.');
            int dot = s.IndexOf('.');
            return dot < 0 ? 0 : (s.Length - dot - 1);
        }

        public static decimal FloorToStep(decimal qty, decimal stepSize, int quantityPrecision)
        {
            if (stepSize <= 0m) return decimal.Round(qty, quantityPrecision, MidpointRounding.ToZero);
            var units = qty / stepSize;
            var floored = Math.Floor(units) * stepSize;
            return decimal.Round(floored, quantityPrecision, MidpointRounding.ToZero);
        }

        public static decimal FloorToTick(decimal price, decimal tickSize, int pricePrecision)
        {
            if (tickSize <= 0m) return decimal.Round(price, pricePrecision, MidpointRounding.ToZero);
            var units = price / tickSize;
            var floored = Math.Floor(units) * tickSize;
            return decimal.Round(floored, pricePrecision, MidpointRounding.ToZero);
        }

        public static decimal CeilToTick(decimal price, decimal tickSize, int pricePrecision)
        {
            if (tickSize <= 0m) return decimal.Round(price, pricePrecision, MidpointRounding.ToZero);
            var units = price / tickSize;
            var ceiled = Math.Ceiling(units) * tickSize;
            return decimal.Round(ceiled, pricePrecision, MidpointRounding.ToZero);
        }

        private static decimal Dec(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);
        private long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _timeOffsetMs;

        private string Sign(string query)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_apiSecret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(query));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private async Task EnsureTimeSyncAsync()
        {
            if (_timeOffsetMs != 0 && (DateTime.UtcNow - _lastSyncUtc) < TimeSpan.FromMinutes(10))
                return;

            var url = $"{_baseUrl}/api/v3/time";
            var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            long serverMs = doc.RootElement.GetProperty("serverTime").GetInt64();
            long localMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _timeOffsetMs = serverMs - localMs;
            _lastSyncUtc = DateTime.UtcNow;
        }

        private static void ThrowApiError(System.Net.HttpStatusCode code, string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                int errCode = root.TryGetProperty("code", out var c) ? c.GetInt32() : (int)code;
                string msg = root.TryGetProperty("msg", out var m) ? m.GetString() : body;
                throw new InvalidOperationException($"Binance API error {errCode}: {msg}");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"HTTP {(int)code}: {body}");
            }
        }

        // DTO'lar
        public sealed class BalanceLine
        {
            public string Asset { get; set; } = string.Empty;
            public decimal Free { get; set; }
            public decimal Locked { get; set; }
            public decimal Total => Free + Locked;
        }

        public sealed class ExchangeSymbol
        {
            public string Symbol { get; set; } = string.Empty;
            public string Status { get; set; } = "TRADING";
            public string BaseAsset { get; set; } = string.Empty;
            public string QuoteAsset { get; set; } = string.Empty;
            public decimal StepSize { get; set; }
            public decimal MinQty { get; set; }
            public decimal MinNotional { get; set; }
            public int QuantityPrecision { get; set; } = 8;
            public decimal TickSize { get; set; }
            public int PricePrecision { get; set; } = 8;
        }

        public sealed class OrderResult
        {
            public long OrderId { get; set; }
            public string ClientOrderId { get; set; } = string.Empty;
            public string Symbol { get; set; } = string.Empty;
            public string Side { get; set; } = "BUY";
            public string Status { get; set; } = string.Empty;
            public decimal Price { get; set; }
            public decimal OrigQty { get; set; }
            public decimal ExecutedQty { get; set; }
        }

        public sealed class OpenOrder
        {
            public long OrderId { get; set; }
            public string ClientOrderId { get; set; } = string.Empty;
            public string Symbol { get; set; } = string.Empty;
            public string Side { get; set; } = "BUY";
            public string Status { get; set; } = "NEW";
            public string TimeInForce { get; set; } = "GTC";
            public string Type { get; set; } = "LIMIT";
            public decimal Price { get; set; }
            public decimal OrigQty { get; set; }
            public decimal ExecutedQty { get; set; }
        }
    }
}
