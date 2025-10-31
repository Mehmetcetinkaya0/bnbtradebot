using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace bnbtradebot
{
    /// <summary>
    /// Statik grid kataloğu: 1 → 10.000 USDT aralığında %0,25 artışlarla
    /// BUY kademelerini üretir, JSON'a kaydeder ve yükler.
    /// Her adım tick'e CEIL yapılarak büyütülür (monoton artış garanti).
    /// SAT hedefi: her BUY seviyesinin "bir üstteki BUY seviyesi".
    /// </summary>
    public sealed class GridCatalog
    {
        public sealed class Entry
        {
            public int Index { get; set; }
            public decimal BuyPrice { get; set; }          // Tick'e hizalı BUY
            public decimal? NextSellPrice { get; set; }    // Bir üst kademe (yoksa null)
        }

        public string Symbol { get; set; } = "BNBUSDT";
        public decimal StepPercent { get; set; } = 0.25m;
        public decimal MinPrice { get; set; } = 1m;
        public decimal MaxPrice { get; set; } = 10000m;   // ← 10.000
        public List<Entry> Levels { get; set; } = new();

        [JsonIgnore]
        public string FilePath { get; private set; } = DefaultFilePath;

        public static string DefaultFilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "grid_catalog.json");

        // ---------- Dış API ----------
        public static async Task<GridCatalog> BuildIfMissingOrInvalidAsync(
            BinanceRestClient rest,
            string symbol,
            decimal stepPercent = 0.25m,
            decimal minPrice = 1m,
            decimal maxPrice = 10000m,
            string? filePath = null)
        {
            var p = filePath ?? DefaultFilePath;

            // Varsa yükle; parametreler uyuşuyorsa aynen kullan
            if (File.Exists(p))
            {
                try
                {
                    var loaded = await LoadAsync(p);
                    if (loaded.Symbol == symbol &&
                        loaded.StepPercent == stepPercent &&
                        loaded.MinPrice == minPrice &&
                        loaded.MaxPrice == maxPrice &&
                        loaded.Levels.Count > 1)
                    {
                        loaded.FilePath = p;
                        return loaded;
                    }
                }
                catch { /* yeniden üret */ }
            }

            var info = await rest.GetSymbolInfoAsync(symbol)
                       ?? throw new InvalidOperationException($"{symbol} exchangeInfo alınamadı.");

            var cat = BuildStatic(info, symbol, stepPercent, minPrice, maxPrice);
            await SaveAsync(cat, p);
            cat.FilePath = p;
            return cat;
        }

        public static GridCatalog BuildStatic(
            BinanceRestClient.ExchangeSymbol info,
            string symbol,
            decimal stepPercent,
            decimal minPrice,
            decimal maxPrice)
        {
            if (stepPercent <= 0) throw new ArgumentException("stepPercent > 0 olmalı.");

            var levels = new List<Entry>(capacity: 5000);
            decimal step = stepPercent / 100m;

            // Başlangıcı tick'e CEIL ile hizala (alt sınır üstü)
            decimal p = CeilToTick(minPrice, info.TickSize, info.PricePrecision);

            int idx = 0;
            while (p <= maxPrice)
            {
                levels.Add(new Entry { Index = idx, BuyPrice = p });
                idx++;

                decimal rawNext = p * (1m + step);
                decimal next = CeilToTick(rawNext, info.TickSize, info.PricePrecision);
                if (next <= p) next = p + info.TickSize; // precision güvenliği

                p = next;
            }

            // SAT hedefi: bir üst BUY seviyesi
            for (int i = 0; i < levels.Count; i++)
                levels[i].NextSellPrice = (i + 1 < levels.Count) ? levels[i + 1].BuyPrice : (decimal?)null;

            return new GridCatalog
            {
                Symbol = symbol,
                StepPercent = stepPercent,
                MinPrice = minPrice,
                MaxPrice = maxPrice,
                Levels = levels
            };
        }

        public static async Task SaveAsync(GridCatalog catalog, string filePath)
        {
            var opt = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(catalog, opt);
            await File.WriteAllTextAsync(filePath, json);
        }

        public static async Task<GridCatalog> LoadAsync(string filePath)
        {
            var json = await File.ReadAllTextAsync(filePath);
            var cat = JsonSerializer.Deserialize<GridCatalog>(json)
                      ?? throw new InvalidOperationException("Grid catalog deserialize hatası.");
            cat.FilePath = filePath;
            return cat;
        }

        /// <summary>
        /// Ask fiyatının ALTINDAKİ en yüksek N BUY seviyesini (desc) döner.
        /// </summary>
        public List<decimal> TopNBuysBelow(decimal ask, int count)
        {
            int lo = 0, hi = Levels.Count - 1, pos = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                var midP = Levels[mid].BuyPrice;
                if (midP < ask) { pos = mid; lo = mid + 1; }
                else { hi = mid - 1; }
            }

            var result = new List<decimal>(count);
            if (pos < 0) return result;
            for (int i = pos; i >= 0 && result.Count < count; i--)
                result.Add(Levels[i].BuyPrice);
            return result; // DESC (yüksekten düşüğe)
        }

        /// <summary> Bir BUY fiyatı için "bir üst kademe" SELL fiyatı (varsa) </summary>
        public decimal? NextSellFor(decimal buyPrice)
        {
            int idx = Levels.FindIndex(e => e.BuyPrice == buyPrice);
            if (idx < 0)
            {
                // Küçük tolerans
                idx = Levels.FindIndex(e => Math.Abs(e.BuyPrice - buyPrice) < (e.BuyPrice * 1e-10m + 1e-8m));
            }
            if (idx < 0) return null;
            return Levels[idx].NextSellPrice;
        }

        // ---------- tick yardımcı ----------
        private static decimal CeilToTick(decimal price, decimal tickSize, int pricePrecision)
        {
            if (tickSize <= 0m) return decimal.Round(price, pricePrecision, MidpointRounding.ToZero);
            var units = price / tickSize;
            var ceiled = Math.Ceiling(units) * tickSize;
            return decimal.Round(ceiled, pricePrecision, MidpointRounding.ToZero);
        }
    }
}
