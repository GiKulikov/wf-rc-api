using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Data;
using System.Collections.Concurrent;
using System.Linq;

#region Controller

[ApiController]
[Route("api/item")]
public class ItemController : ControllerBase
{

    private readonly LockService _lockService;

    public ItemController(LockService lockService)
    {
        _lockService = lockService;
    }


    [HttpGet("{name}")]
    public async Task<Dictionary<string, PriceInfo>> GetParts(string name)
    {
        var result = await _lockService.Lock(name);
        return result;
    }
}
[ApiController]
[Route("api")]
public class PingController : ControllerBase
{
    [HttpGet("ping")]
    public ActionResult Ping()
    {
        return Ok();
    }
}


#endregion

#region Services

public class Links
{
    public static string urlApiItem = "https://api.warframe.market/v1/items/";
}
public class HttpService
{
    public readonly HttpClient _client;
    public HttpService()
    {
        _client = new HttpClient();
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "WarframeRelicBot/1.0 (+https://github.com/GiKulikov/wf-rc-api)");
        _client.DefaultRequestHeaders.Accept.ParseAdd(
      "application/json");
        _client.Timeout = TimeSpan.FromSeconds(30);
    }
    public async Task<string> GetStringAsync(string url)
    {
        using var response = await _client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} ({response.StatusCode}). " +
                $"URL: {url}. Response: {body}");
        }

        return body;
    }

}
public class ItemService
{

    private readonly string _urlApiItem = Links.urlApiItem;
    private readonly HttpService _httpService;
    private readonly CacheService _cacheService;
    private readonly ILogger<ItemService> _logger;

    public ItemService(HttpService httpService, CacheService cacheService, ILogger<ItemService> logger)
    {
        _httpService = httpService;
        _cacheService = cacheService;
        _logger = logger;

    }

    public async Task<Dictionary<string, PriceInfo>> Get(string namePart)
    {
        try
        {

            var result = new Dictionary<string, PriceInfo>();


            var key = namePart;

            if (_cacheService.TryGet(key, out PriceInfo priceInfo))
            {
                result[key] = priceInfo;
            }
            else
            {
                var fresh = await GetItem(key);
                if (fresh.Error == null)
                {
                    _cacheService.Set(key, fresh);
                    result[key] = fresh;

                }
                else
                {
                    result[key] = fresh;
                }

            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error occurred for {namePart}: {ex.Message}");
            return new Dictionary<string, PriceInfo>();
        }
    }
    public async Task<PriceInfo> GetItem(string name)
    {


        try
        {
            List<double> avgPrice = new();
            DateTimeOffset? lastItemLive = null;
            double priceSell = 0;
            double priceBuy = 0;
            var timeNow = DateTimeOffset.UtcNow;
            double? volume1h = null;


            var url = _urlApiItem + name + "/statistics";
            var jsonString = await _httpService.GetStringAsync(url);
            var jsonObject = JsonNode.Parse(jsonString);

            var statisticsClose = jsonObject?["payload"]!["statistics_closed"]!["48hours"]!.AsArray();
            var statisticsLive = jsonObject?["payload"]!["statistics_live"]!["48hours"]!.AsArray();

            if (statisticsClose == null || statisticsLive == null)
            {
                return new PriceInfo
                {
                    Error = "No statistics"
                };
            }

            //avg_price за 48h close
            foreach (var item in statisticsClose!)
            {
                avgPrice.Add(item!["avg_price"]!.GetValue<double>());
            }

            // BuyPrice_1h,SellPrice_1h
            var reversed = statisticsLive!.Reverse().ToList();
            var itemSell = reversed.FirstOrDefault(x => x!["order_type"]!.GetValue<string>() == "sell");
            var itemBuy = reversed.FirstOrDefault(x => x!["order_type"]!.GetValue<string>() == "buy");



            foreach (var item in statisticsLive!)
            {
                var itemTime = DateTimeOffset.Parse(item?["datetime"]!.GetValue<string>()!);
                if (timeNow - itemTime < TimeSpan.FromHours(2))
                {
                    if (item?["order_type"]!.GetValue<string>()! == "sell")
                    {
                        priceSell = item!["median"]!.GetValue<double>();


                    }
                    else
                    {
                        priceBuy = item!["median"]!.GetValue<double>();

                    }
                    lastItemLive = itemTime;
                }

            }

            if (priceSell == 0 && itemSell != null)
            {
                priceSell = itemSell["median"]!.GetValue<double>();

            }
            if (priceBuy == 0 && itemBuy != null)
            {
                priceBuy = itemBuy["median"]!.GetValue<double>();
            }

            // volume
            foreach (var a in statisticsClose)
            {
                if (lastItemLive == DateTimeOffset.Parse(a!["datetime"]!.GetValue<string>()))
                {


                    volume1h = a!["volume"]!.GetValue<double>();
                }

            }

            long volume48h = 0;
            foreach (var a in statisticsClose)
            {


                volume48h += a!["volume"]!.GetValue<int>();


            }

            return new PriceInfo
            {
                //close

                AveragePrice_48h = Math.Round(avgPrice.Average(), 2),
                MaxPrice_48h = statisticsClose.Max(x => x!["max_price"]!.GetValue<double>()),
                MinPrice_48h = statisticsClose.Min(x => x!["min_price"]!.GetValue<double>()),
                Volume_1h = volume1h ?? 0,
                Volume_48h = volume48h,


                //live
                BuyPrice_1h = priceBuy,
                SellPrice_1h = priceSell,
                LastUpdate = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {

            _logger.LogError($"Error occurred while fetching item {name}: {ex.Message}");
            return new PriceInfo
            {
                Error = "Item not found",
                LastUpdate = DateTime.UtcNow
            };
        }



    }

}
public class CacheService
{
    private readonly CacheData _cacheData;
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(60);
    public CacheService(CacheData cache)
    {
        _cacheData = cache;
    }
    public bool TryGet(string itemName, out PriceInfo priceInfo)
    {
        if (_cacheData.cache.TryGetValue(itemName, out priceInfo!))
        {
            if (DateTime.UtcNow - priceInfo.LastUpdate < _ttl)
            {
                return true;
            }
            else
            {

                _cacheData.cache.TryRemove(itemName, out _);
                return false;
            }

        }

        return false;
    }

    public void Set(string itemName, PriceInfo priceInfo)
    {
        _cacheData.cache[itemName] = priceInfo;
    }
}
public class LockService
{
    private readonly LockData _lockData;
    private readonly ItemService _itemService;


    public LockService(ItemService itemService, LockData lockData)
    {
        _itemService = itemService;
        _lockData = lockData;
    }

    public async Task<Dictionary<string, PriceInfo>> Lock(string name)
    {
        var parts = new List<string>();

        var nameParts = name.Split(';');
        string nameFrame = nameParts[0];

        foreach (var a in nameParts)
        {
            if (a != nameFrame)
            {
                parts.Add(a);
            }

        }

        for (int i = 0; i < parts.Count; i++)
        {
            parts[i] = nameFrame + "_" + parts[i];
        }

        parts.Add(nameFrame + "_set");
        parts.Sort();
        foreach (var part in parts)
        {
            _lockData.semaphoreSlim.GetOrAdd(part, _ => new SemaphoreSlim(1, 1));
        }
        var result = new Dictionary<string, PriceInfo>();


        foreach (var partOflist in parts)
        {
            var key = _lockData.semaphoreSlim[partOflist];
            await key.WaitAsync();
            try
            {

                foreach (var item in await _itemService.Get(partOflist))
                {
                    result[item.Key] = item.Value;
                }


            }
            finally
            {
                key.Release();
            }
        }

        return result;
    }

}

#endregion

#region Models

public class CacheData
{
    public readonly ConcurrentDictionary<string, PriceInfo> cache = new();
}
public class LockData
{
    public ConcurrentDictionary<string, SemaphoreSlim> semaphoreSlim = new();
}

public class PriceInfo
{
    public string? Error { get; set; }
    public double AveragePrice_48h { get; set; }
    public double MinPrice_48h { get; set; }
    public double MaxPrice_48h { get; set; }
    public double Volume_1h { get; set; }
    public double Volume_48h { get; set; }


    public double BuyPrice_1h { get; set; }
    public double SellPrice_1h { get; set; }
    public DateTime LastUpdate { get; set; }
}

#endregion