using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;

namespace WinIsland
{
    /* 天气。

       数据源是 Open-Meteo（免费、不要 Key、不用注册），定位走 ipwho.is，
       失败退 free.freeipapi.com。城市名手填时用 open-meteo 的 geocoding。

       为什么不用和风天气：它准、也支持中文城市，但要注册、要 Key、免费额度
       按自然月算，超了要钱。这一版不做，接口位置留在 Provider 那一层。

       几条硬规则（都是从参考实现和这次实测里学来的）：

       1) 直连优先，失败才走系统代理。这台机器挂着代理，IP 出口在加州 ——
          一上来就走代理，定位回来会是「圣克拉拉」。先直连，代理只当兜底。
       2) 15 分钟一次（Open-Meteo 的实况本来就 15 分钟更新一次），
          失败后至少隔 2 分钟再试，同一城市最短 30 秒一次。
       3) 定位结果复用 3 小时，别每分钟去问一次「我在哪」。
       4) 拿不到就老实说拿不到：缓存落盘，离线时显示上次那份 + 多久以前。
       5) 所有网络动作都在后台线程，UI 线程只读快照。 */

    /* 天气现象。决定画太阳、月亮、云、雨还是雪。 */
    enum WKind { Sun, Moon, Partly, PartlyNight, Cloud, Fog, Drizzle, Rain, Shower, Snow, Thunder }

    /// <summary>预报里的一天。</summary>
    class WeatherDay
    {
        public string Label = "";   /* 今天 / 明天 / 周四 */
        public string Date = "";
        public double High;
        public double Low;
        public int Code;
        public int Rain = -1;       /* 降水概率 %，-1 = 没有这个数 */
    }

    /// <summary>一次取数的完整结果。字段自带「拿不到时的体面说法」。</summary>
    class WeatherNow
    {
        public bool Has;            /* 有没有拿到过真数据 */
        public bool Stale;          /* 数据过期了（超过 3 个刷新周期没更新成功） */
        public string Place = "";   /* 城市名 */
        public string Region = "";  /* 省/州 · 国 */
        public double Temp;
        public bool HasFeels;
        public double Feels;
        public bool HasHumidity;
        public int Humidity;
        public bool HasWind;
        public double Wind;
        public int Code;
        public bool IsDay = true;
        public bool HasHigh, HasLow;
        public double High, Low;
        public string Sunrise = "";
        public string Sunset = "";
        public List<WeatherDay> Days = new List<WeatherDay>();
        public long At;             /* 拿到这份数据的 unix 秒 */
        public bool Auto;           /* 来自 IP 自动定位 */
        public string Error = "";   /* 最近一次失败的原因，空 = 没失败 */

        public string Describe { get { return Weather.Describe(Code); } }
        public WKind Kind { get { return Weather.Kind(Code, IsDay); } }
        public int TempRound { get { return (int)Math.Round(Temp); } }

        /// <summary>「15°」这种。整数就够，带小数在岛上读不过来。</summary>
        public string TempText { get { return TempRound + "°"; } }
    }

    static class Weather
    {
        /* ------------------------------------------------------------------ 参数 */

        /// <summary>自动刷新间隔。Open-Meteo 的实况本身就是 15 分钟更新一次。</summary>
        public const int IntervalSec = 900;
        /// <summary>失败后的重试间隔。别把一次网络抖动变成连续几十个请求。</summary>
        public const int RetrySec = 120;
        /// <summary>同一城市最短请求间隔，防手贱连点。</summary>
        public const int MinGapSec = 30;
        /// <summary>定位结果的复用时长。</summary>
        const int LocateTtlSec = 3 * 3600;
        /// <summary>过期判据：超过这么久没更新成功就算 Stale。</summary>
        const int StaleSec = IntervalSec * 3;
        /// <summary>抓几天预报。卡片只放 3 天，多抓的留给以后。</summary>
        public const int ForecastDays = 7;
        /// <summary>HTTP 超时。天气是「顺手看一眼」，不能等太久。</summary>
        const int TimeoutMs = 8000;

        const string Ua = "WinIsland/0.1 (weather; +https://github.com/)";

        /* ------------------------------------------------------------------ 状态 */

        static readonly object _gate = new object();
        static readonly WeatherNow _now = new WeatherNow();

        static Thread _worker;
        static bool _started;
        static volatile int _version;          /* 每次数据变化 +1，Layout 拿它判断要不要重画 */

        /* .NET Framework 的 HttpWebRequest 在没有 app.config 的情况下默认只开
           Ssl3/TLS1.0。这个程序是 csc 直接编出来的、连配置文件都没有，于是对
           open-meteo 这种只收 TLS1.2+ 的站，请求会以
           「未能创建 SSL/TLS 安全通道」失败 —— 报错长得像网络不通，其实是
           协议版本没谈成。这里显式打开。（12288 = TLS 1.3，.NET 4.8 才有，
           系统不支持时不会被用到。） */
        static Weather()
        {
            try
            {
                ServicePointManager.SecurityProtocol |=
                    SecurityProtocolType.Tls12
                    | SecurityProtocolType.Tls11
                    | (SecurityProtocolType)12288;
            }
            catch { }
        }

        static long _lastTryAt;                /* 上次开始抓的时刻（unix） */
        static long _lastOkAt;
        static long _lastFailAt;
        static string _lastFailMsg = "";
        static CityPos _located;               /* 缓存的定位结果 */
        static long _locatedAt;
        static string _city = "";               /* 用户指定城市，空 = 自动定位 */
        static CityPos _cityPos;                /* 用户指定城市解析出的坐标 */

        /* ------------------------------------------------------------------ 对外 */

        /// <summary>数据版本。Layout 的 Live 用它判断「该重画了」。</summary>
        public static int Version() { return _version; }

        public static bool Ready { get { return _now.Has; } }

        /// <summary>当前快照的副本。UI 线程拿它画，绝不拿锁去碰网络。</summary>
        public static WeatherNow Current()
        {
            lock (_gate)
            {
                WeatherNow c = new WeatherNow();
                c.Has = _now.Has;
                c.Place = _now.Place;
                c.Region = _now.Region;
                c.Temp = _now.Temp;
                c.HasFeels = _now.HasFeels;
                c.Feels = _now.Feels;
                c.HasHumidity = _now.HasHumidity;
                c.Humidity = _now.Humidity;
                c.HasWind = _now.HasWind;
                c.Wind = _now.Wind;
                c.Code = _now.Code;
                c.IsDay = _now.IsDay;
                c.HasHigh = _now.HasHigh;
                c.High = _now.High;
                c.HasLow = _now.HasLow;
                c.Low = _now.Low;
                c.Sunrise = _now.Sunrise;
                c.Sunset = _now.Sunset;
                c.At = _now.At;
                c.Auto = _now.Auto;
                c.Error = _now.Error;
                c.Days = new List<WeatherDay>();
                for (int i = 0; i < _now.Days.Count; i++)
                {
                    WeatherDay d = _now.Days[i];
                    WeatherDay n = new WeatherDay();
                    n.Label = d.Label; n.Date = d.Date;
                    n.High = d.High; n.Low = d.Low;
                    n.Code = d.Code; n.Rain = d.Rain;
                    c.Days.Add(n);
                }
                long now = Now();
                c.Stale = !c.Has || (now - _now.At) > StaleSec;
                return c;
            }
        }

        /// <summary>用户指定的城市（空 = 自动定位）。</summary>
        public static string City
        {
            get { lock (_gate) { return _city; } }
        }

        /// <summary>最近一次失败的原因；空 = 没失败过。</summary>
        public static string LastError { get { lock (_gate) { return _lastFailMsg; } } }

        /// <summary>上一次成功取数的 unix 秒；0 = 从没成功过。</summary>
        public static long LastOkAt { get { lock (_gate) { return _lastOkAt; } } }

        /* ------------------------------------------------------------------ 启动 */

        /// <summary>读缓存 + 起后台线程。重复调用安全。</summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;

            LoadCache();
            /* 缓存里有城市就沿用它 */
            lock (_gate) { _city = Prefs.WeatherCity; }

            _worker = new Thread(Loop);
            _worker.IsBackground = true;
            _worker.Name = "weather";
            _worker.Start();
        }

        /// <summary>用户改了城市：清掉旧坐标，下一轮立刻重抓。</summary>
        public static void SetCity(string city)
        {
            city = (city == null) ? "" : city.Trim();
            lock (_gate)
            {
                if (_city == city) return;
                _city = city;
                _cityPos = null;
                _located = null;
                _locatedAt = 0;
                _lastTryAt = 0;         /* 不等到下一轮 */
                _lastFailAt = 0;
                _lastFailMsg = "";
            }
            Prefs.WeatherCity = city;
            Prefs.Flush();
            Trace("weather city -> '" + city + "'");
        }

        /// <summary>手动刷新：强制立刻抓一次，但尊重 30 秒最短间隔。</summary>
        public static void Refresh()
        {
            lock (_gate) { _lastTryAt = 0; }
        }

        /* ------------------------------------------------------------------ 线程 */

        static void Loop()
        {
            /* 起来先等一会儿：开机的头几秒系统在忙着起别的东西，
               这会儿抢网络没有意义，还会让冷启动看起来更慢。 */
            Sleep(1800);

            while (true)
            {
                try
                {
                    if (ShouldFetch()) FetchOnce();
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        _lastFailAt = Now();
                        _lastFailMsg = ex.Message;
                    }
                    Trace("weather loop failed: " + ex.Message);
                }

                Sleep(2000);
            }
        }

        static void Sleep(int ms)
        {
            try { Thread.Sleep(ms); } catch { }
        }

        static bool ShouldFetch()
        {
            long now = Now();
            long lastTry, lastOk, lastFail;
            lock (_gate)
            {
                lastTry = _lastTryAt; lastOk = _lastOkAt; lastFail = _lastFailAt;
            }

            if (lastTry > 0 && now - lastTry < MinGapSec) return false;
            if (lastFail > 0 && now - lastFail < RetrySec) return false;
            if (lastOk == 0) return true;                  /* 一次都没成过，去试 */
            return now - lastOk >= IntervalSec;
        }

        /* ------------------------------------------------------------------ 抓一次 */

        static void FetchOnce()
        {
            lock (_gate)
            {
                _lastTryAt = Now();
                _lastFailMsg = "";
            }

            string city;
            lock (_gate) { city = _city; }

            string err;
            WeatherNow got = city.Length == 0 ? FetchAuto(out err) : FetchForCity(city, out err);

            if (got == null)
            {
                lock (_gate)
                {
                    _lastFailAt = Now();
                    _lastFailMsg = err == null || err.Length == 0 ? "取数失败" : err;
                    _now.Error = _lastFailMsg;
                }
                _version++;
                Trace("weather FAIL " + _lastFailMsg);
                return;
            }

            lock (_gate)
            {
                /* 整份换掉。_now 不直接指向 got —— 缓存落盘失败也不该污染显示 */
                CopyInto(_now, got);
                _lastOkAt = got.At;
                _lastFailAt = 0;
                _lastFailMsg = "";
            }
            _version++;

            SaveCache(got);
            Trace("weather OK " + got.Place + " " + got.TempText + " " + got.Describe
                  + " days=" + got.Days.Count + " auto=" + got.Auto);
        }

        static void CopyInto(WeatherNow dst, WeatherNow src)
        {
            dst.Has = src.Has;
            dst.Place = src.Place;
            dst.Region = src.Region;
            dst.Temp = src.Temp;
            dst.HasFeels = src.HasFeels; dst.Feels = src.Feels;
            dst.HasHumidity = src.HasHumidity; dst.Humidity = src.Humidity;
            dst.HasWind = src.HasWind; dst.Wind = src.Wind;
            dst.Code = src.Code;
            dst.IsDay = src.IsDay;
            dst.HasHigh = src.HasHigh; dst.High = src.High;
            dst.HasLow = src.HasLow; dst.Low = src.Low;
            dst.Sunrise = src.Sunrise; dst.Sunset = src.Sunset;
            dst.At = src.At;
            dst.Auto = src.Auto;
            dst.Error = "";
            dst.Days = src.Days;
        }

        /// <summary>空城市 = 自动定位。</summary>
        static WeatherNow FetchAuto(out string err)
        {
            err = "";

            CityPos pos = null;
            bool fromCache = false;
            long now = Now();
            lock (_gate)
            {
                if (_located != null && (now - _locatedAt) < LocateTtlSec)
                {
                    pos = _located;
                    fromCache = true;
                }
            }

            if (pos == null)
            {
                string e;
                pos = Locate(out e);
                if (pos == null)
                {
                    err = e;
                    return null;
                }
                lock (_gate) { _located = pos; _locatedAt = Now(); }
            }

            bool auto = true;
            WeatherNow got = FetchForecast(pos, auto, out err);
            if (got == null && fromCache)
            {
                /* 缓存的定位可能失效了（换了网络），清掉再定位一次 */
                lock (_gate) { _located = null; _locatedAt = 0; }
            }
            return got;
        }

        static WeatherNow FetchForCity(string city, out string err)
        {
            err = "";

            CityPos pos = null;
            lock (_gate) { pos = _cityPos; }

            if (pos == null)
            {
                string e;
                pos = Geocode(city, out e);
                if (pos == null)
                {
                    err = e;
                    return null;
                }
                lock (_gate) { _cityPos = pos; }
            }

            return FetchForecast(pos, false, out err);
        }

        /* ------------------------------------------------------------------ HTTP */

        /* 直连优先，失败才走系统代理。理由在文件头。 */
        static string HttpGet(string url, out string err)
        {
            err = "";
            string body;

            body = TryGet(url, true, out err);
            if (body != null) return body;

            string directErr = err;
            string proxyErr;
            body = TryGet(url, false, out proxyErr);
            if (body != null) return body;

            err = (proxyErr == null || proxyErr.Length == 0)
                ? directErr
                : "直连" + directErr + "；代理" + proxyErr;
            return null;
        }

        /// <summary>direct=true 时明确不用代理（Proxy = null），false 时用系统代理。</summary>
        static string TryGet(string url, bool direct, out string err)
        {
            err = "";
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.UserAgent = Ua;
                req.Timeout = TimeoutMs;
                req.ReadWriteTimeout = TimeoutMs;
                /* 不要求压缩。天气 JSON 只有 1~4KB，gzip 省不下什么；
                   而 open-meteo 经过这台机器上的代理时，gzip 响应是坏的
                   （"块的长度与它的补集不匹配" = GZipStream 解压失败）。
                   这一条是实测出来的：同一台机器、同一个接口，带 gzip 就炸，
                   不带就干干净净 —— 关掉比事后猜哪里坏了省事得多。 */
                req.Headers[HttpRequestHeader.AcceptEncoding] = "identity";
                req.AllowAutoRedirect = true;

                /* 这一行是「直连优先」的全部实现：null = 谁也别替我代理 */
                req.Proxy = direct ? null : WebRequest.DefaultWebProxy;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (System.IO.Stream st = resp.GetResponseStream())
                using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
                {
                    byte[] buf = new byte[8192];
                    int n;
                    while ((n = st.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            catch (WebException we)
            {
                HttpWebResponse r = we.Response as HttpWebResponse;
                err = (r != null) ? ("HTTP " + (int)r.StatusCode) : ShortMsg(we);
                return null;
            }
            catch (Exception ex)
            {
                err = ShortMsg(ex);
                return null;
            }
        }

        static string ShortMsg(Exception ex)
        {
            if (ex == null) return "网络错误";
            string m = ex.Message;
            if (m.Length > 60) m = m.Substring(0, 60);
            return m;
        }

        /* ------------------------------------------------------------------ 定位 */

        class CityPos
        {
            public double Lat, Lon;
            public string Label = "";
            public string Region = "";
        }

        /// <summary>按网络出口定位。主源 ipwho.is，备源 freeipapi。</summary>
        static CityPos Locate(out string err)
        {
            err = "";

            string e1;
            CityPos p = LocateIpWho(out e1);
            if (p != null) return p;

            string e2;
            CityPos q = LocateFreeIp(out e2);
            if (q != null) return q;

            err = "自动定位不可用（" + e1 + "）";
            return null;
        }

        static CityPos LocateIpWho(out string err)
        {
            err = "";
            string body = HttpGet("https://ipwho.is/?lang=zh-CN", out err);
            if (body == null) return null;

            JNode j = Json.TryParse(body);
            if (j == null) { err = "定位响应格式无效"; return null; }
            if (!j.Bool("success", true)) { err = "定位服务返回失败"; return null; }

            CityPos p = new CityPos();
            double? lat = j.Num("latitude");
            double? lon = j.Num("longitude");
            if (!lat.HasValue || !lon.HasValue) { err = "定位结果缺少坐标"; return null; }

            p.Lat = lat.Value;
            p.Lon = lon.Value;
            p.Label = FirstNonEmpty(j.Str("city"), j.Str("region"), "当地");
            p.Region = Join(" · ", j.Str("region"), j.Str("country"));
            if (!Valid(p)) { err = "定位结果坐标无效"; return null; }
            return p;
        }

        static CityPos LocateFreeIp(out string err)
        {
            err = "";
            string body = HttpGet("https://free.freeipapi.com/api/v1/json", out err);
            if (body == null) return null;

            JNode j = Json.TryParse(body);
            if (j == null) { err = "定位响应格式无效"; return null; }

            double? lat = j.Num("latitude");
            double? lon = j.Num("longitude");
            if (!lat.HasValue || !lon.HasValue) { err = "定位结果缺少坐标"; return null; }

            CityPos p = new CityPos();
            p.Lat = lat.Value;
            p.Lon = lon.Value;
            p.Label = FirstNonEmpty(j.Str("cityName"), j.Str("regionName"), j.Str("countryName"), "当地");
            p.Region = Join(" · ", j.Str("regionName"), j.Str("countryName"));
            if (!Valid(p)) { err = "定位结果坐标无效"; return null; }
            return p;
        }

        static bool Valid(CityPos p)
        {
            return p != null
                && p.Label != null && p.Label.Trim().Length > 0
                && !double.IsNaN(p.Lat) && !double.IsInfinity(p.Lat)
                && !double.IsNaN(p.Lon) && !double.IsInfinity(p.Lon)
                && p.Lat >= -90 && p.Lat <= 90
                && p.Lon >= -180 && p.Lon <= 180;
        }

        /* ------------------------------------------------------------------ 城市搜索 */

        /* 中文城市的几个坑，都是照着参考实现学来的：

           1) 「广州市」查不到 —— 地理库里那条叫「广州」。所以要去后缀再查一次。
           2) 「新疆」查不到 —— 库里只有城市。所以省级名要换成省会。
           3) 两字简称会撞上同名小镇 —— 所以同时查「XX市」，精确名优先。
           4) 机场、车站也叫「北京」—— 按 feature_code 排掉。 */

        static readonly char[] AdminSuffixes = new char[] { '市', '县', '区', '省' };

        static readonly string[,] ProvinceCapitals = new string[,]
        {
            {"北京","北京"},{"天津","天津"},{"上海","上海"},{"重庆","重庆"},
            {"河北","石家庄"},{"山西","太原"},{"辽宁","沈阳"},{"吉林","长春"},
            {"黑龙江","哈尔滨"},{"江苏","南京"},{"浙江","杭州"},{"安徽","合肥"},
            {"福建","福州"},{"江西","南昌"},{"山东","济南"},{"河南","郑州"},
            {"湖北","武汉"},{"湖南","长沙"},{"广东","广州"},{"海南","海口"},
            {"四川","成都"},{"贵州","贵阳"},{"云南","昆明"},{"陕西","西安"},
            {"甘肃","兰州"},{"青海","西宁"},{"台湾","台北"},{"内蒙古","呼和浩特"},
            {"广西","南宁"},{"西藏","拉萨"},{"宁夏","银川"},{"新疆","乌鲁木齐"},
            {"香港","香港"},{"澳门","澳门"}
        };

        static bool IsCjk(string s)
        {
            if (s == null || s.Length == 0) return false;
            for (int i = 0; i < s.Length; i++)
                if (s[i] < '\u4e00' || s[i] > '\u9fff') return false;
            return true;
        }

        /// <summary>去掉行政区后缀后的基础名；去掉后不足两字（如「津市」）就保留原名。</summary>
        public static string BaseName(string name)
        {
            if (name == null) return "";
            if (!IsCjk(name) || name.Length < 2) return name;
            char last = name[name.Length - 1];
            for (int i = 0; i < AdminSuffixes.Length; i++)
            {
                if (last == AdminSuffixes[i])
                {
                    string b = name.Substring(0, name.Length - 1);
                    if (b.Length >= 2) return b;
                }
            }
            return name;
        }

        static string ProvinceCapital(string name)
        {
            if (name == null || name.Length == 0) return null;

            string[] suffixes = new string[] {
                "维吾尔自治区", "壮族自治区", "回族自治区", "自治区", "特别行政区", "省", "市"
            };
            string shortName = name;
            for (int i = 0; i < suffixes.Length; i++)
            {
                if (name.Length > suffixes[i].Length && name.EndsWith(suffixes[i]))
                {
                    shortName = name.Substring(0, name.Length - suffixes[i].Length);
                    break;
                }
            }

            for (int i = 0; i < ProvinceCapitals.GetLength(0); i++)
                if (ProvinceCapitals[i, 0] == shortName) return ProvinceCapitals[i, 1];
            return null;
        }

        /// <summary>一个输入要查的若干写法。先查哪个、后查哪个都在这里定。</summary>
        public static List<string> CityQueries(string name)
        {
            List<string> q = new List<string>();
            q.Add(name);
            if (name.Length < 2 || !IsCjk(name)) return q;

            string capital = ProvinceCapital(name);
            if (capital != null && capital != BaseName(name)) q.Add(capital);

            string baseName = BaseName(name);
            if (baseName != name)
            {
                q.Add(baseName);                    /* 广州市 -> 广州 */
            }
            else if (capital == null)
            {
                /* 两字简称：地理库里常常只收「苏州市」这种带市的全称。
                   省名不加 —— 「新疆市」是不存在的东西，查了也只是白跑一趟。 */
                q.Insert(0, name + "市");
            }
            return q;
        }

        /// <summary>地理库里的机场、车站、旅馆不作为天气地点。</summary>
        static bool SkipFeature(string code)
        {
            if (code == null) return false;
            string[] prefixes = new string[] { "AIR", "RSTN", "BUS", "PRT", "HTL", "SCH", "HSP" };
            for (int i = 0; i < prefixes.Length; i++)
                if (code.StartsWith(prefixes[i])) return true;
            return false;
        }

        /// <summary>按城市名查坐标。返回最优的一个；同名地点多于一个时给出让用户确认的说法。</summary>
        static CityPos Geocode(string name, out string err)
        {
            err = "";
            List<CityPos> found = SearchCities(name, out err);
            if (found == null) return null;
            if (found.Count == 0)
            {
                err = "没找到「" + name + "」，试试完整市名或英文名";
                return null;
            }
            if (found.Count == 1) return found[0];

            /* 有多个：如果第一个是精确同名，就用它。否则让用户自己选。 */
            CityPos best = found[0];
            if (best.Label == name || best.Label == BaseName(name)) return best;

            err = "「" + name + "」有多个同名地点：" + DescribeTop(found, 3);
            return null;
        }

        static string DescribeTop(List<CityPos> list, int n)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < list.Count && i < n; i++)
            {
                if (i > 0) sb.Append(" / ");
                sb.Append(list[i].Label);
                if (list[i].Region.Length > 0) sb.Append("(").Append(list[i].Region).Append(")");
            }
            return sb.ToString();
        }

        /// <summary>查一串写法，合并结果并按「名字对不对得上 + 人口」排序。</summary>
        static List<CityPos> SearchCities(string name, out string err)
        {
            err = "";
            List<CityPos> all = new List<CityPos>();
            List<long> pops = new List<long>();

            string lastErr = "";
            List<string> queries = CityQueries(name);
            for (int qi = 0; qi < queries.Count; qi++)
            {
                StringBuilder url = new StringBuilder();
                url.Append("https://geocoding-api.open-meteo.com/v1/search?name=");
                url.Append(Uri.EscapeDataString(queries[qi]));
                url.Append("&count=10&language=zh&format=json");

                string e;
                string body = HttpGet(url.ToString(), out e);
                if (body == null) { lastErr = e; continue; }

                JNode j = Json.TryParse(body);
                if (j == null) { lastErr = "城市搜索响应格式无效"; continue; }

                JNode results = j.Get("results");
                if (results == null || results.Kind != JNode.KindArr) continue;

                for (int i = 0; i < results.Items.Count; i++)
                {
                    JNode it = results.Items[i];
                    string feature = it.Str("feature_code");
                    if (SkipFeature(feature)) continue;

                    string label = it.Str("name");
                    if (label == null) continue;
                    label = label.Trim();
                    if (label.Length == 0) continue;

                    double? lat = it.Num("latitude");
                    double? lon = it.Num("longitude");
                    if (!lat.HasValue || !lon.HasValue) continue;

                    CityPos p = new CityPos();
                    p.Lat = lat.Value;
                    p.Lon = lon.Value;
                    p.Label = label;
                    p.Region = JoinDistinct(" · ", label, it.Str("admin2"), it.Str("admin1"), it.Str("country"));
                    if (!Valid(p)) continue;

                    long pop = 0;
                    double? pn = it.Num("population");
                    if (pn.HasValue && pn.Value > 0) pop = (long)pn.Value;

                    all.Add(p);
                    pops.Add(pop);
                }
            }

            if (all.Count == 0 && lastErr.Length > 0) { err = lastErr; return null; }
            return Rank(name, all, pops);
        }

        /// <summary>名字与输入（或去掉后缀的基础名）一致的排前面，其次按人口；同名且坐标接近的去重。</summary>
        static List<CityPos> Rank(string name, List<CityPos> list, List<long> pops)
        {
            string baseName = BaseName(name);

            /* 简单插入排序：一次就 10~30 条，不值得为它引什么 */
            for (int i = 1; i < list.Count; i++)
            {
                CityPos cp = list[i];
                long cpop = pops[i];
                bool cexact = Exact(cp.Label, name, baseName);
                int j = i - 1;
                while (j >= 0)
                {
                    bool jexact = Exact(list[j].Label, name, baseName);
                    bool moveUp = cexact && !jexact;
                    if (!moveUp && cexact == jexact && cpop > pops[j]) moveUp = true;
                    if (!moveUp) break;
                    list[j + 1] = list[j]; pops[j + 1] = pops[j];
                    j--;
                }
                list[j + 1] = cp; pops[j + 1] = cpop;
            }

            List<CityPos> ranked = new List<CityPos>();
            for (int i = 0; i < list.Count; i++)
            {
                bool dup = false;
                for (int k = 0; k < ranked.Count; k++)
                {
                    if (ranked[k].Label == list[i].Label
                        && Math.Abs(ranked[k].Lat - list[i].Lat) < 0.05
                        && Math.Abs(ranked[k].Lon - list[i].Lon) < 0.05)
                    {
                        dup = true; break;
                    }
                }
                if (!dup) ranked.Add(list[i]);
            }
            return ranked;
        }

        static bool Exact(string label, string name, string baseName)
        {
            if (label == name || label == baseName) return true;
            return BaseName(label) == baseName;
        }

        /* ------------------------------------------------------------------ 预报 */

        static WeatherNow FetchForecast(CityPos pos, bool auto, out string err)
        {
            err = "";

            StringBuilder url = new StringBuilder();
            url.Append("https://api.open-meteo.com/v1/forecast");
            url.Append("?latitude=").Append(Fmt(pos.Lat, 4));
            url.Append("&longitude=").Append(Fmt(pos.Lon, 4));
            url.Append("&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,wind_speed_10m");
            url.Append("&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset");
            url.Append("&timezone=auto&forecast_days=").Append(ForecastDays.ToString(CultureInfo.InvariantCulture));

            string body = HttpGet(url.ToString(), out err);
            if (body == null) return null;

            JNode j = Json.TryParse(body);
            if (j == null) { err = "天气响应格式无效"; return null; }

            WeatherNow n = new WeatherNow();
            n.Has = true;
            n.Place = pos.Label;
            n.Region = pos.Region;
            n.Auto = auto;
            n.At = Now();

            JNode cur = j.Get("current");
            if (cur == null) { err = "天气数据缺少实况"; return null; }

            double? t = cur.Num("temperature_2m");
            if (!t.HasValue) { err = "天气数据缺少温度"; return null; }
            n.Temp = t.Value;

            double? feels = cur.Num("apparent_temperature");
            if (feels.HasValue) { n.HasFeels = true; n.Feels = feels.Value; }

            double? rh = cur.Num("relative_humidity_2m");
            if (rh.HasValue) { n.HasHumidity = true; n.Humidity = (int)Math.Round(rh.Value); }

            double? ws = cur.Num("wind_speed_10m");
            if (ws.HasValue) { n.HasWind = true; n.Wind = ws.Value; }

            double? code = cur.Num("weather_code");
            n.Code = code.HasValue ? (int)Math.Round(code.Value) : 3;

            double? isDay = cur.Num("is_day");
            n.IsDay = !isDay.HasValue || isDay.Value >= 0.5;

            JNode daily = j.Get("daily");
            if (daily != null) ParseDaily(n, daily);

            return n;
        }

        static void ParseDaily(WeatherNow n, JNode daily)
        {
            JNode dates = daily.Get("time");
            JNode codes = daily.Get("weather_code");
            JNode highs = daily.Get("temperature_2m_max");
            JNode lows = daily.Get("temperature_2m_min");
            JNode rains = daily.Get("precipitation_probability_max");
            JNode rises = daily.Get("sunrise");
            JNode sets = daily.Get("sunset");

            int count = dates == null ? 0 : dates.Items.Count;

            for (int i = 0; i < count; i++)
            {
                string date = dates.Items[i].AsString();
                WeatherDay d = new WeatherDay();
                d.Date = date == null ? "" : date;
                d.Label = DayLabel(i, d.Date);

                double? h = ArrayNum(highs, i);
                double? l = ArrayNum(lows, i);
                double? c = ArrayNum(codes, i);
                double? r = ArrayNum(rains, i);

                d.High = h.HasValue ? h.Value : 0;
                d.Low = l.HasValue ? l.Value : 0;
                d.Code = c.HasValue ? (int)Math.Round(c.Value) : 3;
                d.Rain = r.HasValue ? (int)Math.Round(r.Value) : -1;

                n.Days.Add(d);
            }

            if (n.Days.Count > 0)
            {
                n.HasHigh = true; n.High = n.Days[0].High;
                n.HasLow = true; n.Low = n.Days[0].Low;
            }

            /* 今天的日出日落：接口给的是 "2026-11-22T07:12" 这种本地时间 */
            if (rises != null && rises.Items.Count > 0) n.Sunrise = ClockOf(rises.Items[0].AsString());
            if (sets != null && sets.Items.Count > 0) n.Sunset = ClockOf(sets.Items[0].AsString());
        }

        static double? ArrayNum(JNode arr, int i)
        {
            if (arr == null || arr.Kind != JNode.KindArr) return null;
            if (i < 0 || i >= arr.Items.Count) return null;
            return arr.Items[i].AsNumber();
        }

        /// <summary>"2026-11-22T07:12" -> "07:12"。</summary>
        static string ClockOf(string iso)
        {
            if (iso == null || iso.Length < 16) return "";
            return iso.Substring(11, 5);
        }

        static string DayLabel(int index, string date)
        {
            if (index == 0) return "今天";
            if (index == 1) return "明天";
            if (index == 2) return "后天";

            /* 只有 date 是 yyyy-MM-dd 才推星期，否则退回日期 */
            if (date != null && date.Length >= 10)
            {
                int y, m, d;
                if (int.TryParse(date.Substring(0, 4), out y)
                    && int.TryParse(date.Substring(5, 2), out m)
                    && int.TryParse(date.Substring(8, 2), out d))
                {
                    try
                    {
                        DateTime dt = new DateTime(y, m, d);
                        string[] w = new string[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
                        return w[(int)dt.DayOfWeek];
                    }
                    catch { }
                }
                return date.Substring(5);
            }
            return "—";
        }

        /* ------------------------------------------------------------------ WMO 码 */

        /// <summary>WMO 天气代码 → 图标类型。day 决定画太阳还是月亮。</summary>
        public static WKind Kind(int code, bool day)
        {
            switch (code)
            {
                case 0: return day ? WKind.Sun : WKind.Moon;
                case 1:
                case 2: return day ? WKind.Partly : WKind.PartlyNight;
                case 3: return WKind.Cloud;
                case 45:
                case 48: return WKind.Fog;
                case 51:
                case 53:
                case 55:
                case 56:
                case 57: return WKind.Drizzle;
                case 61:
                case 63:
                case 65:
                case 66:
                case 67: return WKind.Rain;
                case 71:
                case 73:
                case 75:
                case 77: return WKind.Snow;
                case 80:
                case 81:
                case 82: return WKind.Shower;
                case 85:
                case 86: return WKind.Snow;
                case 95:
                case 96:
                case 97:
                case 99: return WKind.Thunder;
                default: return WKind.Cloud;
            }
        }

        /// <summary>WMO 天气代码 → 中文。</summary>
        public static string Describe(int code)
        {
            switch (code)
            {
                case 0: return "晴";
                case 1: return "晴间多云";
                case 2: return "多云";
                case 3: return "阴";
                case 45:
                case 48: return "雾";
                case 51:
                case 53: return "毛毛雨";
                case 55: return "强毛毛雨";
                case 56:
                case 57:
                case 66:
                case 67: return "冻雨";
                case 61: return "小雨";
                case 63: return "中雨";
                case 65: return "大雨";
                case 71: return "小雪";
                case 73: return "中雪";
                case 75: return "大雪";
                case 77: return "米雪";
                case 80: return "阵雨";
                case 81: return "强阵雨";
                case 82: return "暴雨";
                case 85: return "阵雪";
                case 86: return "强阵雪";
                case 95: return "雷阵雨";
                case 97: return "强雷阵雨";
                case 96:
                case 99: return "雷暴伴冰雹";
                default: return "多云";
            }
        }

        /* ------------------------------------------------------------------ 缓存 */

        /* weather.txt 一行一条 key=value。城市名可能是中文，值一律 base64 ——
           和 Store 里其它 store 的做法一致，省得纠结编码。 */

        /// <summary>只读盘：把 weather.txt 读进内存。Start() 内部会调它，
        /// 离线渲染的验证程序也直接调它（那条路不能碰网络）。</summary>
        public static void LoadCache()
        {
            try
            {
                string[] lines = AppDir.ReadLines("weather.txt");
                if (lines == null || lines.Length == 0) return;

                WeatherNow n = new WeatherNow();
                List<WeatherDay> days = new List<WeatherDay>();
                WeatherDay day = null;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();

                    if (k == "at") n.At = ParseLong(v);
                    else if (k == "place") n.Place = AppDir.Decode(v);
                    else if (k == "region") n.Region = AppDir.Decode(v);
                    else if (k == "temp") n.Temp = ParseDouble(v);
                    else if (k == "feels") { n.HasFeels = true; n.Feels = ParseDouble(v); }
                    else if (k == "humidity") { n.HasHumidity = true; n.Humidity = (int)ParseLong(v); }
                    else if (k == "wind") { n.HasWind = true; n.Wind = ParseDouble(v); }
                    else if (k == "code") n.Code = (int)ParseLong(v);
                    else if (k == "isday") n.IsDay = v != "0";
                    else if (k == "high") { n.HasHigh = true; n.High = ParseDouble(v); }
                    else if (k == "low") { n.HasLow = true; n.Low = ParseDouble(v); }
                    else if (k == "rise") n.Sunrise = v;
                    else if (k == "set") n.Sunset = v;
                    else if (k == "auto") n.Auto = v != "0";
                    else if (k == "day")
                    {
                        if (day != null) days.Add(day);
                        day = new WeatherDay();
                        day.Label = AppDir.Decode(v);
                    }
                    else if (day != null)
                    {
                        if (k == "d") day.Date = v;
                        else if (k == "hi") day.High = ParseDouble(v);
                        else if (k == "lo") day.Low = ParseDouble(v);
                        else if (k == "c") day.Code = (int)ParseLong(v);
                        else if (k == "r") day.Rain = (int)ParseLong(v);
                    }
                }
                if (day != null) days.Add(day);
                n.Days = days;

                if (n.Place.Length == 0) return;    /* 缓存里没有像样的东西，别用 */
                n.Has = true;

                lock (_gate)
                {
                    CopyInto(_now, n);
                    _lastOkAt = n.At;
                }
                Trace("weather cache loaded " + n.Place + " " + n.TempText + " at=" + n.At);
            }
            catch (Exception ex)
            {
                Trace("weather cache load failed: " + ex.Message);
            }
        }

        static void SaveCache(WeatherNow n)
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("at=" + n.At.ToString(CultureInfo.InvariantCulture));
                lines.Add("place=" + AppDir.Encode(n.Place));
                lines.Add("region=" + AppDir.Encode(n.Region));
                lines.Add("temp=" + Fmt(n.Temp, 2));
                if (n.HasFeels) lines.Add("feels=" + Fmt(n.Feels, 2));
                if (n.HasHumidity) lines.Add("humidity=" + n.Humidity.ToString(CultureInfo.InvariantCulture));
                if (n.HasWind) lines.Add("wind=" + Fmt(n.Wind, 2));
                lines.Add("code=" + n.Code.ToString(CultureInfo.InvariantCulture));
                lines.Add("isday=" + (n.IsDay ? "1" : "0"));
                if (n.HasHigh) lines.Add("high=" + Fmt(n.High, 2));
                if (n.HasLow) lines.Add("low=" + Fmt(n.Low, 2));
                if (n.Sunrise.Length > 0) lines.Add("rise=" + n.Sunrise);
                if (n.Sunset.Length > 0) lines.Add("set=" + n.Sunset);
                lines.Add("auto=" + (n.Auto ? "1" : "0"));

                for (int i = 0; i < n.Days.Count; i++)
                {
                    WeatherDay d = n.Days[i];
                    lines.Add("day=" + AppDir.Encode(d.Label));
                    lines.Add("d=" + d.Date);
                    lines.Add("hi=" + Fmt(d.High, 2));
                    lines.Add("lo=" + Fmt(d.Low, 2));
                    lines.Add("c=" + d.Code.ToString(CultureInfo.InvariantCulture));
                    lines.Add("r=" + d.Rain.ToString(CultureInfo.InvariantCulture));
                }

                AppDir.WriteLines("weather.txt", lines);
            }
            catch (Exception ex)
            {
                Trace("weather cache save failed: " + ex.Message);
            }
        }

        /* ------------------------------------------------------------------ 小工具 */

        static long Now()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>数据是多久以前拿的，说人话。</summary>
        public static string AgeText(long at)
        {
            if (at <= 0) return "从未更新";
            long d = Now() - at;
            if (d < 0) d = 0;
            if (d < 90) return "刚刚更新";
            if (d < 3600) return (d / 60) + " 分钟前";
            if (d < 86400) return (d / 3600) + " 小时前";
            return (d / 86400) + " 天前";
        }

        static string Fmt(double v, int digits)
        {
            return Math.Round(v, digits).ToString(CultureInfo.InvariantCulture);
        }

        static long ParseLong(string s)
        {
            long v;
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        static double ParseDouble(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        static string FirstNonEmpty(string a, string b, string c)
        {
            return FirstNonEmpty(a, b, c, "");
        }

        static string FirstNonEmpty(string a, string b, string c, string d)
        {
            if (!string.IsNullOrEmpty(a) && a.Trim().Length > 0) return a.Trim();
            if (!string.IsNullOrEmpty(b) && b.Trim().Length > 0) return b.Trim();
            if (!string.IsNullOrEmpty(c) && c.Trim().Length > 0) return c.Trim();
            if (!string.IsNullOrEmpty(d) && d.Trim().Length > 0) return d.Trim();
            return "";
        }

        static string Join(string sep, string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b == null ? "" : b.Trim();
            if (string.IsNullOrEmpty(b)) return a.Trim();
            if (a.Trim() == b.Trim()) return a.Trim();
            return a.Trim() + sep + b.Trim();
        }

        static string JoinDistinct(string sep, string skip, params string[] parts)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p == null) continue;
                p = p.Trim();
                if (p.Length == 0 || p == skip) continue;
                if (sb.Length > 0) sb.Append(sep);
                sb.Append(p);
            }
            return sb.ToString();
        }

        internal static void Trace(string s)
        {
            Action<string> d = Diag;
            if (d != null) d(s);
        }

        /// <summary>埋点出口，由 IslandWindow 接上（和 Layout.Diag 同样的做法）。</summary>
        public static Action<string> Diag;
    }

    /* ---------------------------------------------------------------------- JSON */

    /* 极简 JSON。为什么自己写：这个程序不引任何 NuGet 包，而 .NET Framework 4.8
       自带的 System.Web.Extensions 里那个 JavaScriptSerializer 能用是能用，
       但编译脚本里那行 /r:System.Web.Extensions.dll 是为了别的地方引的，
       拿它解析天气还得先定义一堆 DTO，不如一个 200 行的解析器来得直白。

       只做值模型，不做序列化 —— 天气数据是只读的。 */

    sealed class JNode
    {
        public const int KindObj = 0, KindArr = 1, KindStr = 2, KindNum = 3, KindBool = 4, KindNull = 5;

        public int Kind;
        public Dictionary<string, JNode> Fields;
        public List<JNode> Items;
        public string Text;
        public double Number;
        public bool Flag;

        public JNode Get(string key)
        {
            if (Kind != KindObj || Fields == null) return null;
            JNode v;
            return Fields.TryGetValue(key, out v) ? v : null;
        }

        public JNode At(int i)
        {
            if (Kind != KindArr || Items == null) return null;
            if (i < 0 || i >= Items.Count) return null;
            return Items[i];
        }

        public string AsString()
        {
            if (Kind == KindStr) return Text;
            if (Kind == KindNum) return Number.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        public double? AsNumber()
        {
            if (Kind == KindNum) return Number;
            /* 有些服务把数字写成字符串 */
            if (Kind == KindStr && Text != null)
            {
                double v;
                if (double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            }
            return null;
        }

        public string Str(string key)
        {
            JNode n = Get(key);
            return n == null ? null : n.AsString();
        }

        public double? Num(string key)
        {
            JNode n = Get(key);
            return n == null ? null : n.AsNumber();
        }

        public bool Bool(string key, bool fallback)
        {
            JNode n = Get(key);
            if (n == null) return fallback;
            if (n.Kind == KindBool) return n.Flag;
            if (n.Kind == KindNum) return n.Number != 0;
            if (n.Kind == KindStr) return n.Text == "true" || n.Text == "1";
            return fallback;
        }
    }

    static class Json
    {
        /// <summary>解析失败返回 null，不抛。天气数据坏了不该把线程带走。</summary>
        public static JNode TryParse(string s)
        {
            if (s == null) return null;
            try
            {
                int i = 0;
                JNode v = ParseValue(s, ref i, 0);
                return v;
            }
            catch
            {
                return null;
            }
        }

        const int MaxDepth = 40;

        static JNode ParseValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("json too deep");
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("eof");

            char c = s[i];
            if (c == '{') return ParseObject(s, ref i, depth);
            if (c == '[') return ParseArray(s, ref i, depth);
            if (c == '"')
            {
                JNode n = new JNode();
                n.Kind = JNode.KindStr;
                n.Text = ParseString(s, ref i);
                return n;
            }
            if (c == 't' || c == 'f')
            {
                JNode n = new JNode();
                n.Kind = JNode.KindBool;
                if (c == 't' && i + 4 <= s.Length && s.Substring(i, 4) == "true")
                { n.Flag = true; i += 4; return n; }
                if (c == 'f' && i + 5 <= s.Length && s.Substring(i, 5) == "false")
                { n.Flag = false; i += 5; return n; }
                throw new FormatException("bad literal");
            }
            if (c == 'n')
            {
                if (i + 4 <= s.Length && s.Substring(i, 4) == "null")
                {
                    i += 4;
                    JNode n = new JNode();
                    n.Kind = JNode.KindNull;
                    return n;
                }
                throw new FormatException("bad null");
            }
            return ParseNumber(s, ref i);
        }

        static JNode ParseObject(string s, ref int i, int depth)
        {
            JNode n = new JNode();
            n.Kind = JNode.KindObj;
            n.Fields = new Dictionary<string, JNode>();
            i++;                                  /* { */
            SkipWs(s, ref i);

            if (i < s.Length && s[i] == '}') { i++; return n; }

            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("key expected");
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("colon expected");
                i++;
                JNode v = ParseValue(s, ref i, depth + 1);
                n.Fields[key] = v;
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("eof in object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return n; }
                throw new FormatException("object sep expected");
            }
        }

        static JNode ParseArray(string s, ref int i, int depth)
        {
            JNode n = new JNode();
            n.Kind = JNode.KindArr;
            n.Items = new List<JNode>();
            i++;                                  /* [ */
            SkipWs(s, ref i);

            if (i < s.Length && s[i] == ']') { i++; return n; }

            while (true)
            {
                JNode v = ParseValue(s, ref i, depth + 1);
                n.Items.Add(v);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("eof in array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return n; }
                throw new FormatException("array sep expected");
            }
        }

        static string ParseString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("quote expected");
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"') { i++; return sb.ToString(); }
                if (c == '\\')
                {
                    i++;
                    if (i >= s.Length) throw new FormatException("eof in escape");
                    char e = s[i];
                    if (e == '"') sb.Append('"');
                    else if (e == '\\') sb.Append('\\');
                    else if (e == '/') sb.Append('/');
                    else if (e == 'b') sb.Append('\b');
                    else if (e == 'f') sb.Append('\f');
                    else if (e == 'n') sb.Append('\n');
                    else if (e == 'r') sb.Append('\r');
                    else if (e == 't') sb.Append('\t');
                    else if (e == 'u')
                    {
                        if (i + 4 >= s.Length) throw new FormatException("bad \\u");
                        string hex = s.Substring(i + 1, 4);
                        int code = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        sb.Append((char)code);
                        i += 4;
                    }
                    else throw new FormatException("bad escape");
                    i++;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            throw new FormatException("unterminated string");
        }

        static JNode ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '-' || c == '+') i++;
                else break;
            }
            if (i == start) throw new FormatException("number expected");

            string text = s.Substring(start, i - start);
            double v;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                throw new FormatException("bad number");

            JNode n = new JNode();
            n.Kind = JNode.KindNum;
            n.Number = v;
            return n;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { i++; continue; }
                break;
            }
        }
    }
}
