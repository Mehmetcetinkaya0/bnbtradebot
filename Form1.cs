using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace bnbtradebot
{
    public partial class Form1 : Form
    {
        private const string SYMBOL = "BNBUSDT";
        private const decimal STEP_PERCENT = 0.25m;      // sabit grid aralığı
        private const int TARGET_BUY_COUNT = 30;         // aynı anda tutulacak BUY adedi
        private const decimal DEFAULT_QUOTE_PER_LEVEL = 10m; // USDT/kademe (UI)
        private const bool REPLENISH_BUY_ONLY_AFTER_SELL = true; // SELL dolmadan aynı seviyeye yeni BUY yazma

        // Servisler
        private BinancePriceFeed? _feed;
        private BinanceUserDataStream? _uds;
        private BinanceRestClient? _rest;
        private BinanceRestClient.ExchangeSymbol? _symInfo;

        // Fiyat
        private decimal? _lastAsk;
        private DateTime? _lastAskUtc;

        // Kasa (profit label)
        private decimal _initialWalletUsdt;
        private decimal _currentWalletUsdt;

        // Bot
        private bool _botRunning = false;
        private CancellationTokenSource? _maintCts;
        private Task? _maintTask;

        // Katalog
        private GridCatalog? _catalog;

        // Açık emirler
        private readonly Dictionary<long, OrderRow> _openOrders = new();

        // Thread-safe erişim için kilit
        private readonly object _ordersLock = new();

        // Aynı fiyatı iki kez yerleştirmemek için "rezervasyon" seti
        private readonly HashSet<decimal> _inflightBuyPrices = new();
        private readonly object _inflightLock = new();

        // Çalışırken değiştirilebilen USDT/kademe
        private decimal _quotePerLevelUsdt = DEFAULT_QUOTE_PER_LEVEL;

        private sealed class OrderRow
        {
            public long OrderId;
            public string Side = "";
            public string Status = "";
            public decimal Price;
            public decimal OrigQty;
            public decimal ExecutedQty;
            public string ClientOrderId = "";
        }

        public Form1()
        {
            InitializeComponent();
            this.Load += Form1_Load;
        }

        // ------------------- FORM LOAD -------------------
        private async void Form1_Load(object? sender, EventArgs e)
        {
            // Start/Stop
            btnStartStop.Click += async (_, __) => await ToggleStartStopAsync();

            // "Güncelle" butonu – botu durdurmadan USDT/kademe güncelle
            btnApplyQuote.Click += (_, __) => ApplyQuotePerLevelFromUi(triggerMaintain: true);

            // REST + exchangeInfo
            _rest = new BinanceRestClient(Apiler.TESTNET_API_KEY, Apiler.TESTNET_API_SECRET, useTestnet: true);
            _symInfo = await _rest.GetSymbolInfoAsync(SYMBOL)
                    ?? throw new InvalidOperationException($"{SYMBOL} exchangeInfo alınamadı.");

            // Fiyat WS
            _feed = new BinancePriceFeed(useTestnet: true);
            _feed.OnTicker += t =>
            {
                _lastAsk = t.Ask;
                _lastAskUtc = DateTime.UtcNow;

                if (lblBnbPrice.InvokeRequired) lblBnbPrice.BeginInvoke(new Action(() => lblBnbPrice.Text = t.Ask.ToString("F4")));
                else lblBnbPrice.Text = t.Ask.ToString("F4");

                UpdateProfitLabelSafe();
            };
            _feed.OnStatus += s =>
            {
                string last = s.LastMessageUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "-";
                string err = string.IsNullOrEmpty(s.Error) ? "" : $" | Err: {s.Error}";
                string ep = string.IsNullOrEmpty(s.Endpoint) ? "" : $" | {s.Endpoint}";
                string text = $"PriceWS: {s.State} | Last: {last} | Reconn: {s.ReconnectCount}{err}{ep}";
                if (lblConnStatus.InvokeRequired) lblConnStatus.BeginInvoke(new Action(() => lblConnStatus.Text = text));
                else lblConnStatus.Text = text;
            };
            await _feed.StartAsync();

            // User Data Stream
            _uds = new BinanceUserDataStream(Apiler.TESTNET_API_KEY, Apiler.TESTNET_API_SECRET, useTestnet: true);
            _uds.OnBalances += snapshot => _ = UpdateWalletFromSnapshotAsync(snapshot);
            _uds.OnOrderUpdate += upd => OnOrderUpdateFromUds(upd);
            await _uds.StartAsync();

            EnsureOrderGridColumns(gridOpenBuys);
            EnsureOrderGridColumns(gridOpenSells);

            // UI’daki USDT/kademe değerini aktif hâle getir + minNotional’a zorla
            ApplyQuotePerLevelFromUi(triggerMaintain: false);
        }

        // ------------------- START/STOP -------------------
        private async Task ToggleStartStopAsync()
        {
            if (_botRunning) await StopBotAsync();
            else await StartBotAsync();
        }

        private async Task StartBotAsync()
        {
            // Katalog (1→10.000, %0.25)
            _catalog = await GridCatalog.BuildIfMissingOrInvalidAsync(
                rest: _rest!, symbol: SYMBOL,
                stepPercent: STEP_PERCENT,
                minPrice: 1m, maxPrice: 10000m,
                filePath: null
            );

            await RefreshOpenOrdersAsync();

            _maintCts = new CancellationTokenSource();
            _maintTask = Task.Run(() => MaintainLadderLoopAsync(_maintCts.Token));
            _botRunning = true;

            if (btnStartStop.InvokeRequired) btnStartStop.BeginInvoke(new Action(() => btnStartStop.Text = "Stop"));
            else btnStartStop.Text = "Stop";
        }

        private async Task StopBotAsync()
        {
            try { _maintCts?.Cancel(); } catch { }
            try { if (_maintTask != null) await _maintTask; } catch { }
            _maintCts?.Dispose(); _maintCts = null; _maintTask = null;

            _botRunning = false;
            if (btnStartStop.InvokeRequired) btnStartStop.BeginInvoke(new Action(() => btnStartStop.Text = "Start"));
            else btnStartStop.Text = "Start";
        }

        // ------------------- LADDER BAKIM -------------------
        private async Task MaintainLadderLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await EnsureTargetBuySetAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Maintain error: " + ex.Message); }

                try { await Task.Delay(4000, token); } catch { break; }
            }
        }

        /// <summary>
        /// Hedef aktif BUY setini korur. (envanter-farkındalığı: SELL sayısı kadar BUY azaltılır)
        /// </summary>
        private async Task EnsureTargetBuySetAsync(CancellationToken token)
        {
            if (!_botRunning || _rest == null || _symInfo == null || _catalog == null) return;

            decimal ask = await GetBnbAskPriceAsync();

            // Envanter farkındalığı: SELL açıkken yeni BUY ile doldurma
            var snap = GetOrdersSnapshot();
            int openSellCount = snap.Count(x => x.Side == "SELL" && (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED"));

            int desiredBuyCount = TARGET_BUY_COUNT;
            if (REPLENISH_BUY_ONLY_AFTER_SELL)
                desiredBuyCount = Math.Max(0, TARGET_BUY_COUNT - openSellCount);

            var targetPricesDesc = _catalog.TopNBuysBelow(ask, desiredBuyCount); // DESC

            // Kopya BUY varsa temizle
            await CancelDuplicateBuysByPriceAsync();

            // Mevcut açık BUY'lar
            var openBuyOrders = snap
                .Where(x => x.Side == "BUY" && (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED"))
                .OrderByDescending(x => x.Price)
                .ToList();
            var openPrices = new HashSet<decimal>(openBuyOrders.Select(o => o.Price));

            // Fazla BUY'ları iptal et
            if (openBuyOrders.Count > desiredBuyCount)
            {
                int toCancel = openBuyOrders.Count - desiredBuyCount;

                var notInTarget = openBuyOrders.Where(o => !targetPricesDesc.Contains(o.Price)).ToList();
                var cancelList = new List<OrderRow>(notInTarget);

                if (cancelList.Count < toCancel)
                {
                    var stillNeed = toCancel - cancelList.Count;
                    var inTargetButFarthest = openBuyOrders
                        .Where(o => targetPricesDesc.Contains(o.Price))
                        .OrderBy(o => o.Price) // en düşükler en uzaktır
                        .Take(stillNeed);
                    cancelList.AddRange(inTargetButFarthest);
                }

                foreach (var o in cancelList.Take(toCancel))
                {
                    try { await _rest.CancelOrderAsync(SYMBOL, o.OrderId); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Cancel excess BUY fail: " + ex.Message); }
                }
                return; // bu turda sadece azalt
            }

            // Eksik BUY'ları yerleştir (in‑flight korumalı)
            if (openBuyOrders.Count < desiredBuyCount)
            {
                foreach (var price in targetPricesDesc)
                {
                    if (token.IsCancellationRequested) break;

                    bool blocked;
                    lock (_inflightLock)
                    {
                        blocked = _inflightBuyPrices.Contains(price) || openPrices.Contains(price);
                        if (!blocked) _inflightBuyPrices.Add(price);
                    }
                    if (blocked) continue;

                    try
                    {
                        decimal qty = ComputeQtyForPrice(price, _symInfo);
                        var cid = $"GBUY_{price.ToString(CultureInfo.InvariantCulture).Replace(".", "")}_{(DateTime.UtcNow.Ticks % 100000)}";
                        await _rest.PlaceLimitOrderAsync(SYMBOL, "BUY", price, qty, _symInfo, "GTC", cid);
                        // NEW geldiğinde inflight'tan düşürülür (OnOrderUpdate içinde)
                    }
                    catch (Exception ex)
                    {
                        lock (_inflightLock) { _inflightBuyPrices.Remove(price); }
                        System.Diagnostics.Debug.WriteLine($"BUY place fail @ {price}: " + ex.Message);
                    }

                    try { await Task.Delay(120, token); } catch { break; }
                }
            }
        }

        /// <summary> Aynı fiyattan birden fazla BUY varsa fazlalıkları iptal eder. </summary>
        private async Task CancelDuplicateBuysByPriceAsync()
        {
            try
            {
                var dupGroups = GetOrdersSnapshot()
                    .Where(x => x.Side == "BUY" && (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED"))
                    .GroupBy(x => x.Price)
                    .Where(g => g.Count() > 1);

                foreach (var g in dupGroups)
                {
                    var keep = g.First().OrderId;
                    foreach (var extra in g.Where(x => x.OrderId != keep))
                    {
                        try { await _rest!.CancelOrderAsync(SYMBOL, extra.OrderId); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Cancel dup fail: " + ex.Message); }
                    }
                }
            }
            catch { }
        }

        // ------------------- MİKTAR HESABI -------------------
        private decimal ComputeQtyForPrice(decimal price, BinanceRestClient.ExchangeSymbol info)
        {
            decimal quotePerLevel = _quotePerLevelUsdt;

            // minNotional uyumu: notional = qty * price >= minNotional
            var qTarget = quotePerLevel / price;
            var qFromNotional = info.MinNotional > 0m ? info.MinNotional / price : 0m;

            // minQty'yi step'e hizala (alt sınır)
            var minStepQty = BinanceRestClient.FloorToStep(info.MinQty, info.StepSize, info.QuantityPrecision);
            if (minStepQty <= 0m) minStepQty = info.MinQty;

            var qRaw = Math.Max(qTarget, Math.Max(qFromNotional, minStepQty));

            // Notional şartını garanti etmek için STEP'e **CEIL** uygula
            var units = Math.Ceiling(qRaw / info.StepSize);
            var q = units * info.StepSize;

            q = decimal.Round(q, info.QuantityPrecision, MidpointRounding.ToZero);
            return q;
        }

        // ------------------- ORDER EVENTS -------------------
        private void OnOrderUpdateFromUds(BinanceUserDataStream.OrderUpdate u)
        {
            if (!string.Equals(u.Symbol, SYMBOL, StringComparison.OrdinalIgnoreCase))
                return;

            // Inflight BUY rezervasyonunu temizle
            if (u.Side == "BUY" && (u.Status == "NEW" || u.Status == "CANCELED" || u.Status == "REJECTED"))
            {
                lock (_inflightLock) { _inflightBuyPrices.Remove(u.Price); }
            }

            if (u.Status == "NEW" || u.Status == "PARTIALLY_FILLED")
                AddOrUpdateRow(u.OrderId, u.Side, u.Status, u.Price, u.OrigQty, u.ExecutedQty, u.ClientOrderId);
            else
                RemoveRow(u.OrderId); // FILLED/CANCELED/EXPIRED/REJECTED

            // BUY FILLED → bir üst kademeye SELL
            if (u.Side == "BUY" && u.Status == "FILLED")
                _ = PlaceSellAtNextCatalogLevelAsync(u.Price, u.ExecutedQty);

            RefreshOrderGridsSafe();
        }

        private async Task PlaceSellAtNextCatalogLevelAsync(decimal buyPrice, decimal qty)
        {
            try
            {
                if (_rest == null || _symInfo == null) return;

                decimal sellPrice;
                var next = _catalog?.NextSellFor(buyPrice);
                if (next.HasValue) sellPrice = next.Value;
                else
                {
                    var raw = buyPrice * (1m + (STEP_PERCENT / 100m)); // emniyetli fallback
                    sellPrice = BinanceRestClient.CeilToTick(raw, _symInfo.TickSize, _symInfo.PricePrecision);
                }

                var cid = $"GSELL_{sellPrice.ToString(CultureInfo.InvariantCulture).Replace(".", "")}_{(DateTime.UtcNow.Ticks % 100000)}";
                await _rest.PlaceLimitOrderAsync(SYMBOL, "SELL", sellPrice, qty, _symInfo, "GTC", cid);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SELL place fail: " + ex.Message);
            }
        }

        // ------------------- SNAPSHOT & GRID -------------------
        private async Task RefreshOpenOrdersAsync()
        {
            if (_rest == null) return;
            var list = await _rest.GetOpenOrdersAsync(SYMBOL);

            lock (_ordersLock)
            {
                _openOrders.Clear();
                foreach (var o in list)
                {
                    _openOrders[o.OrderId] = new OrderRow
                    {
                        OrderId = o.OrderId,
                        Side = o.Side,
                        Status = o.Status,
                        Price = o.Price,
                        OrigQty = o.OrigQty,
                        ExecutedQty = o.ExecutedQty,
                        ClientOrderId = o.ClientOrderId
                    };
                }
            }
            RefreshOrderGridsSafe();
        }

        private List<OrderRow> GetOrdersSnapshot()
        {
            lock (_ordersLock)
            {
                return _openOrders.Values.ToList();
            }
        }

        private void AddOrUpdateRow(long orderId, string side, string status, decimal price, decimal origQty, decimal execQty, string clientId)
        {
            lock (_ordersLock)
            {
                if (_openOrders.TryGetValue(orderId, out var row))
                {
                    row.Side = side; row.Status = status; row.Price = price;
                    row.OrigQty = origQty; row.ExecutedQty = execQty; row.ClientOrderId = clientId;
                }
                else
                {
                    _openOrders[orderId] = new OrderRow
                    {
                        OrderId = orderId,
                        Side = side,
                        Status = status,
                        Price = price,
                        OrigQty = origQty,
                        ExecutedQty = execQty,
                        ClientOrderId = clientId
                    };
                }
            }
        }

        private void RemoveRow(long orderId)
        {
            lock (_ordersLock) { _openOrders.Remove(orderId); }
        }

        private void EnsureOrderGridColumns(DataGridView? grid)
        {
            if (grid == null) return;
            if (grid.Columns.Count > 0) return;

            grid.AutoGenerateColumns = false;
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colOrderId", HeaderText = "OrderId", Width = 110 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colPrice", HeaderText = "Fiyat", Width = 110 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colQty", HeaderText = "Miktar", Width = 110 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colExec", HeaderText = "Dolum", Width = 110 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colStatus", HeaderText = "Durum", Width = 110 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "colCID", HeaderText = "ClientId", Width = 160 });
        }

        private void RefreshOrderGridsSafe()
        {
            if (this.InvokeRequired) this.BeginInvoke(new Action(RefreshOrderGrids));
            else RefreshOrderGrids();
        }

        private void RefreshOrderGrids()
        {
            if (gridOpenBuys != null) { EnsureOrderGridColumns(gridOpenBuys); gridOpenBuys.Rows.Clear(); }
            if (gridOpenSells != null) { EnsureOrderGridColumns(gridOpenSells); gridOpenSells.Rows.Clear(); }

            var rows = GetOrdersSnapshot();
            foreach (var kv in rows.OrderByDescending(x => x.Price))
            {
                var cells = new object[]
                {
                    kv.OrderId,
                    kv.Price.ToString("0.0000"),
                    kv.OrigQty.ToString("0.########"),
                    kv.ExecutedQty.ToString("0.########"),
                    kv.Status,
                    kv.ClientOrderId
                };

                try
                {
                    if (kv.Side == "BUY") gridOpenBuys?.Rows.Add(cells);
                    else if (kv.Side == "SELL") gridOpenSells?.Rows.Add(cells);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Grid row add fail: " + ex.Message);
                }
            }
        }

        // ------------------- CÜZDAN & PROFIT -------------------
        private async Task UpdateWalletFromSnapshotAsync(IReadOnlyDictionary<string, BinanceUserDataStream.Balance> map)
        {
            try
            {
                var usdt = map.TryGetValue("USDT", out var u) ? u : new BinanceUserDataStream.Balance();
                var bnb = map.TryGetValue("BNB", out var b) ? b : new BinanceUserDataStream.Balance();

                decimal ask = await GetBnbAskPriceAsync();
                decimal usdtTotal = usdt.Total;
                decimal bnbTotal = bnb.Total;
                decimal walletTotal = usdtTotal + bnbTotal * ask;

                string text = $"USDT: {usdtTotal:0.00} | BNB: {bnbTotal:0.####} | Toplam: {walletTotal:0.00} USDT";
                if (lblWalletSummary.InvokeRequired) lblWalletSummary.BeginInvoke(new Action(() => lblWalletSummary.Text = text));
                else lblWalletSummary.Text = text;

                if (_initialWalletUsdt == 0m) _initialWalletUsdt = walletTotal;
                _currentWalletUsdt = walletTotal;

                UpdateProfitLabelSafe();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Wallet update error: " + ex.Message);
            }
        }

        private void UpdateProfitLabelSafe()
        {
            if (_initialWalletUsdt <= 0m || _currentWalletUsdt <= 0m)
            {
                if (lblProfit.InvokeRequired) lblProfit.BeginInvoke(new Action(() => lblProfit.Text = ""));
                else lblProfit.Text = "";
                return;
            }

            var diff = _currentWalletUsdt - _initialWalletUsdt;
            var percent = (_initialWalletUsdt == 0m) ? 0m : (diff / _initialWalletUsdt) * 100m;

            string text = percent >= 0 ? $"+{percent:F2}% kârda" : $"{percent:F2}% zararda";

            if (lblProfit.InvokeRequired)
            {
                lblProfit.BeginInvoke(new Action(() =>
                {
                    lblProfit.Text = text;
                    lblProfit.ForeColor = percent >= 0 ? System.Drawing.Color.Green : System.Drawing.Color.Red;
                }));
            }
            else
            {
                lblProfit.Text = text;
                lblProfit.ForeColor = percent >= 0 ? System.Drawing.Color.Green : System.Drawing.Color.Red;
            }
        }

        // ------------------- FİYAT & USDT/kademe -------------------
        private async Task<decimal> GetBnbAskPriceAsync()
        {
            if (_lastAsk.HasValue && _lastAskUtc.HasValue &&
                DateTime.UtcNow - _lastAskUtc.Value < TimeSpan.FromSeconds(10))
                return _lastAsk.Value;

            var price = await _rest!.GetAskPriceAsync(SYMBOL);
            _lastAsk = price; _lastAskUtc = DateTime.UtcNow;
            return price;
        }

        /// <summary>
        /// TextBox’tan USDT/kademe’yi okuyup aktif değeri günceller.
        /// minNotional altına düşerse otomatik yukarı yuvarlar.
        /// </summary>
        private void ApplyQuotePerLevelFromUi(bool triggerMaintain)
        {
            decimal val = DEFAULT_QUOTE_PER_LEVEL;

            var raw = (txtStepPercent.Text ?? "").Trim();

            if (!decimal.TryParse(raw, NumberStyles.Any, CultureInfo.CurrentCulture, out val) || val <= 0m)
            {
                if (!decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out val) || val <= 0m)
                    val = DEFAULT_QUOTE_PER_LEVEL;
            }

            // minNotional zorlaması (örn. testnette 5 USDT)
            if (_symInfo != null && val < _symInfo.MinNotional)
                val = _symInfo.MinNotional;

            _quotePerLevelUsdt = val;

            // UI normalize
            if (txtStepPercent.InvokeRequired) txtStepPercent.BeginInvoke(new Action(() => txtStepPercent.Text = val.ToString(CultureInfo.CurrentCulture)));
            else txtStepPercent.Text = val.ToString(CultureInfo.CurrentCulture);

            // Hemen bir bakım turu tetikle (isteğe bağlı)
            if (triggerMaintain && _maintCts != null && !_maintCts.IsCancellationRequested)
                _ = EnsureTargetBuySetAsync(_maintCts.Token);
        }

        // ------------------- Form kapanış -------------------
        protected override async void OnFormClosing(FormClosingEventArgs e)
        {
            try { _maintCts?.Cancel(); } catch { }
            try { if (_maintTask != null) await _maintTask; } catch { }

            if (_uds != null) await _uds.StopAsync();
            if (_feed != null) await _feed.StopAsync();
            _rest?.Dispose();

            base.OnFormClosing(e);
        }
    }
}
