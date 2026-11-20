using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace WinIsland
{
    /* 一份歌词：按时间排序的 (秒, 文本)。只读，跨线程传引用即可。 */
    class LyricDoc
    {
        public double[] Times = new double[0];
        public string[] Texts = new string[0];
        public string Source = "";
        public int Count { get { return Texts.Length; } }
    }

    /* 歌词抓取。只允许在后台线程调用（阻塞 HTTP）。

       两路数据源，先 lrclib（无需鉴权、直接给带时间轴的 LRC），
       拿不到再走网易云（搜歌 -> 取 lyric 字段）。

       注意：SMTC 不提供歌词，这是完全独立的一条链路。 */
    static class Lyric
    {
        const string UA = "WinIsland/1.0 (local desktop widget)";
        const int TimeoutMs = 6000;

        /* 埋点出口，由 IslandWindow 接上（和 Layout / Transfer 同一个路子）。
           排查「这首歌为什么没词」时，这条日志能直接看出卡在第几次尝试。 */
        public static Action<string> Diag;

        static void Log(string s)
        {
            if (Diag != null) Diag(s);
        }

        static readonly Regex TimeTag =
            new Regex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

        /* ---------------------------------------------------------------- 对外 */

        /// <summary>
        /// 抓歌词。duration 用来在两个地方提高准确度：lrclib 的精确查询（它按 ±2 秒
        /// 判断是不是同一版），以及多个候选之间排序。传 0 表示不知道。
        /// </summary>
        public static LyricDoc Fetch(string title, string artist, double duration)
        {
            if (title == null || title.Trim().Length == 0) return null;

            string clean = CleanTitle(title);
            string hard = HardClean(title);
            string a = artist == null ? "" : artist.Trim();

            int dur = 0;
            if (duration > 1 && duration < 3600) dur = (int)Math.Round(duration);

            LyricDoc doc;

            /* 顺序说明：先在「带时长」的精确查询上试，命中率最高也最不容易串到
               现场版/翻唱；再退回不带时长的精确查询 —— 这一步和改动前完全一样，
               保证不会因为新加的时长参数把原来能拿到的弄丢。 */

            doc = TryLrclibGet(clean, a, dur);
            if (Ok(doc)) { Log("lyric hit #1 lrclib get dur lines=" + doc.Count); return doc; }

            doc = TryLrclibGet(clean, a, 0);
            if (Ok(doc)) { Log("lyric hit #2 lrclib get lines=" + doc.Count); return doc; }

            /* /api/search 是「包含」匹配，比 /api/get 宽松得多。
               这一步替换掉的是原来那两次「不带 artist_name」的请求 ——
               lrclib 现在把 artist_name 列为必填，缺了直接 400，
               那两次尝试从来不可能成功。 */
            doc = TryLrclibSearch(clean, a, dur);
            if (Ok(doc)) { Log("lyric hit #3 lrclib search lines=" + doc.Count); return doc; }

            if (a.Length > 0)
            {
                doc = TryLrclibSearch(clean, "", dur);
                if (Ok(doc)) { Log("lyric hit #4 lrclib search no-artist lines=" + doc.Count); return doc; }
            }

            if (hard.Length > 0 && hard != clean)
            {
                doc = TryLrclibGet(hard, a, 0);
                if (Ok(doc)) { Log("lyric hit #5 lrclib get hard lines=" + doc.Count); return doc; }

                if (a.Length > 0)
                {
                    doc = TryLrclibSearch(hard, "", dur);
                    if (Ok(doc)) { Log("lyric hit #6 lrclib search hard lines=" + doc.Count); return doc; }
                }
            }

            /* 网易云兜底 */
            doc = TryNetease(clean, a, dur);
            if (Ok(doc)) { Log("lyric hit #7 netease lines=" + doc.Count); return doc; }

            if (hard.Length > 0 && hard != clean)
            {
                doc = TryNetease(hard, a, dur);
                if (Ok(doc)) { Log("lyric hit #8 netease hard lines=" + doc.Count); return doc; }
            }

            Log("lyric miss clean='" + clean + "' hard='" + hard + "' artist='" + a + "'");
            return null;
        }

        static bool Ok(LyricDoc d) { return d != null && d.Count > 0; }

        /* 当前该显示哪一行。位置还没起来时给第一行，位置缺失（pos<=0）也一样，
           总比空着好 —— 播放器不给时间轴时这就是最好的结果。 */
        public static string LineAt(LyricDoc doc, double pos)
        {
            if (doc == null || doc.Texts.Length == 0) return "";
            if (pos <= 0) return doc.Texts[0];

            int idx = 0;
            for (int i = 0; i < doc.Times.Length; i++)
            {
                if (doc.Times[i] <= pos) idx = i;
                else break;
            }
            return doc.Texts[idx];
        }

        /* 「倒带 (Live)」「屋顶 - 周杰伦」「01. 情歌」「【高音质】」这类后缀
           会把搜索带偏。轻度清洗只剥「结尾」的括号，保留曲名本体。 */
        public static string CleanTitle(string title)
        {
            if (title == null) return "";
            string s = title.Trim();

            int dash = s.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash > 1) s = s.Substring(0, dash).Trim();

            int[] opens = new int[] { s.IndexOf('('), s.IndexOf('（') };
            foreach (int o in opens)
            {
                if (o > 0)
                {
                    int close = s.LastIndexOf(')');
                    int close2 = s.LastIndexOf('）');
                    int c = Math.Max(close, close2);
                    if (c > o) s = s.Substring(0, o).Trim();
                }
            }
            return s.Length == 0 ? title.Trim() : s;
        }

        /* 更狠的一版：去掉任意位置的括号内容（含【】「」）、前导曲目序号、
           以及常见的版本尾词。B 站那种视频标题基本只能靠这个捞一下。 */
        public static string HardClean(string title)
        {
            if (title == null) return "";
            string s = title.Trim();

            /* 成对括号整段删掉（多轮，处理嵌套「(Live)(Remix)」） */
            string[] pairs = new string[] { "()", "（）", "【】", "[]", "「」", "《》" };
            for (int pass = 0; pass < 4; pass++)
            {
                int before = s.Length;
                for (int i = 0; i < pairs.Length; i++)
                {
                    char open = pairs[i][0], close = pairs[i][1];
                    int a = s.IndexOf(open);
                    int b = s.LastIndexOf(close);
                    if (a >= 0 && b > a) s = (s.Substring(0, a) + s.Substring(b + 1)).Trim();
                }
                if (s.Length == before) break;
            }

            /* " - 歌手" / "—" 尾巴 */
            int dash = s.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash > 1) s = s.Substring(0, dash).Trim();
            int em = s.LastIndexOf("—", StringComparison.Ordinal);
            if (em > 1) s = s.Substring(0, em).Trim();

            /* 前导 "01." / "01、" / "1 " */
            int i2 = 0;
            while (i2 < s.Length && char.IsDigit(s[i2])) i2++;
            if (i2 > 0 && i2 < s.Length)
            {
                char sep = s[i2];
                if (sep == '.' || sep == '、' || sep == ' ' || sep == '-')
                {
                    string rest = s.Substring(i2 + 1).Trim();
                    if (rest.Length > 0) s = rest;
                }
            }

            /* 常见无意义尾词 */
            string[] tails = new string[] { "官方版", "官方MV", "MV", "HQ", "高音质", "无损", "完整版", "现场版" };
            for (int i = 0; i < tails.Length; i++)
            {
                if (s.EndsWith(tails[i], StringComparison.OrdinalIgnoreCase))
                {
                    string t = s.Substring(0, s.Length - tails[i].Length).Trim();
                    if (t.Length > 0) s = t;
                }
            }

            return s.Length == 0 ? title.Trim() : s;
        }

        /* --------------------------------------------------------------- lrclib */

        /* 精确查询。artist_name 现在是必填（缺了框架直接 400），
           所以空艺人时干脆不发请求。 */
        static LyricDoc TryLrclibGet(string track, string artist, int duration)
        {
            try
            {
                if (track.Length == 0 || artist.Length == 0) return null;

                string url = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(track)
                             + "&artist_name=" + Uri.EscapeDataString(artist);
                if (duration > 0) url += "&duration=" + duration;

                string json = Get(url);
                if (json == null) return null;
                return DocOf(json, "lrclib");
            }
            catch (Exception)
            {
                return null;
            }
        }

        /* 模糊搜索。track_name 是「包含」匹配，拿回一个数组，
           按「时长接近 + 艺人像」挑一条最合适的 —— 而不是闭眼取第一条。 */
        static LyricDoc TryLrclibSearch(string track, string artist, int duration)
        {
            try
            {
                if (track.Length == 0) return null;

                string url = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(track);
                if (artist.Length > 0) url += "&artist_name=" + Uri.EscapeDataString(artist);

                string json = Get(url);
                if (json == null) return null;

                List<string> recs = TopLevelObjects(json);
                LyricDoc best = null;
                int bestRank = int.MaxValue;

                for (int i = 0; i < recs.Count && i < 20; i++)
                {
                    string rec = recs[i];

                    string lrc;
                    if (!TopField(rec, "syncedLyrics", out lrc)) continue;
                    if (lrc == null || lrc.Trim().Length == 0) continue;

                    int rank = i;                       /* 同分时按原顺序 */

                    string dText;
                    double rd = 0;
                    if (TopField(rec, "duration", out dText))
                        double.TryParse(dText, NumberStyles.Float, CultureInfo.InvariantCulture, out rd);

                    if (duration > 0 && rd > 0)
                    {
                        double diff = Math.Abs(rd - duration);
                        /* 差太多不排除，只是排后面：宁可给一张时长对不上的词，
                           也比显示「无同步歌词」强 */
                        rank += diff > 5 ? 40 : (int)diff;
                    }

                    string ra;
                    if (artist.Length > 0 && TopField(rec, "artistName", out ra)
                        && ra != null && ra.Length > 0 && !Similar(artist, ra))
                        rank += 12;

                    if (rank >= bestRank) continue;

                    LyricDoc doc = ParseLrc(lrc);
                    if (doc == null) continue;
                    bestRank = rank;
                    best = doc;
                }

                if (best != null) best.Source = "lrclib-search";
                return best;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /* 一条 lrclib 记录 -> LyricDoc。只要 syncedLyrics：
           单行 UI 显示不了纯文本歌词（见 style-spec 10.15 已知局限）。 */
        static LyricDoc DocOf(string recordJson, string source)
        {
            string lrc;
            if (!TopField(recordJson, "syncedLyrics", out lrc)) return null;
            if (lrc == null || lrc.Trim().Length == 0) return null;

            LyricDoc doc = ParseLrc(lrc);
            if (doc != null) doc.Source = source;
            return doc;
        }

        /* --------------------------------------------------------------- 网易云 */

        /* 网易云兜底。

           这条链以前是**完全死的**：原来用正则抓响应里第一个 "id"，而歌曲对象
           里嵌套着 album.artist，它的 id 排在歌曲自己的 id 前面（实测取到 0），
           于是永远在查 /api/song/lyric?id=0，返回固定的占位符
           「[00:00.00]暂无歌词」，再被下面的行数检查挡掉 —— 一次都没成功过。

           现在只认 songs 数组里的对象，字段在**对象顶层**取（跳过嵌套），
           按「歌名相似 + 时长接近」挑候选，再逐个取词。 */
        /* internal 而不是 private：验收程序要单独验证这条链（它曾经 100% 失效），
           在同程序集里直接调用比"祈祷它轮到第 7 次尝试"可靠。 */
        internal static LyricDoc TryNetease(string track, string artist, int duration)
        {
            try
            {
                string q = artist.Length > 0 ? track + " " + artist : track;
                string search = "https://music.163.com/api/search/get/web?type=1&limit=5&s="
                                + Uri.EscapeDataString(q);
                string sj = Get(search, "https://music.163.com/");
                if (sj == null) return null;

                string songs = SliceValue(sj, "songs", '[', ']');
                if (songs == null) return null;

                List<string> recs = TopLevelObjects(songs);
                List<string> ids = new List<string>();
                List<int> ranks = new List<int>();

                for (int i = 0; i < recs.Count; i++)
                {
                    string rec = recs[i];

                    string name;
                    if (!TopField(rec, "name", out name)) continue;
                    if (name == null || name.Length == 0) continue;

                    /* 网易云搜索对「乱码歌名」也会硬返回最相似的一条，
                       于是我们会把别人的歌词显示出来。先核一下它匹配到的歌名：
                       不像就直接放弃，宁可显示「无同步歌词」。 */
                    bool hit = Similar(track, name);
                    if (!hit && artist.Length > 0) hit = Similar(artist, name);
                    if (!hit) continue;

                    string idText;
                    if (!TopField(rec, "id", out idText)) continue;
                    if (idText == null || idText.Length == 0) continue;

                    int rank = i;

                    string dText;
                    double ms = 0;
                    if (TopField(rec, "duration", out dText))
                        double.TryParse(dText, NumberStyles.Float, CultureInfo.InvariantCulture, out ms);

                    if (duration > 0 && ms > 0)          /* 网易云的 duration 是毫秒 */
                    {
                        double diff = Math.Abs(ms / 1000.0 - duration);
                        rank += diff > 5 ? 40 : (int)diff;
                    }

                    ids.Add(idText);
                    ranks.Add(rank);
                    if (ids.Count >= 5) break;
                }

                /* 候选最多 5 条，冒泡足够 */
                for (int i = 0; i < ranks.Count; i++)
                {
                    for (int j = i + 1; j < ranks.Count; j++)
                    {
                        if (ranks[j] >= ranks[i]) continue;
                        int tr = ranks[i]; ranks[i] = ranks[j]; ranks[j] = tr;
                        string ti = ids[i]; ids[i] = ids[j]; ids[j] = ti;
                    }
                }

                /* 每次取词都是一次 HTTP，最多试两个候选 */
                int tries = ids.Count < 2 ? ids.Count : 2;
                for (int i = 0; i < tries; i++)
                {
                    string lj = Get("https://music.163.com/api/song/lyric?id=" + ids[i]
                                    + "&lv=-1&kv=-1&tv=-1", "https://music.163.com/");
                    if (lj == null) continue;

                    string lrcObj = SliceValue(lj, "lrc", '{', '}');
                    if (lrcObj == null) continue;

                    string lrc;
                    if (!TopField(lrcObj, "lyric", out lrc)) continue;
                    if (lrc == null || lrc.Trim().Length == 0) continue;

                    LyricDoc doc = ParseLrc(lrc);
                    /* 纯音乐占位常常只剩一行字，这种也别当成歌词 */
                    if (doc == null || doc.Count < 3) continue;
                    doc.Source = "netease";
                    return doc;
                }
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /* 歌名相似度：一方包含另一方，或二元组重合率 >= 0.5。
           中英混排、全半角、标点差异都能穿过去。 */
        static bool Similar(string a, string b)
        {
            string x = Norm(a), y = Norm(b);
            if (x.Length == 0 || y.Length == 0) return false;
            if (x.IndexOf(y, StringComparison.Ordinal) >= 0) return true;
            if (y.IndexOf(x, StringComparison.Ordinal) >= 0) return true;

            int hit = 0, total = 0;
            for (int i = 0; i + 1 < x.Length; i++)
            {
                total++;
                if (y.IndexOf(x.Substring(i, 2), StringComparison.Ordinal) >= 0) hit++;
            }
            if (total == 0) return false;
            return (double)hit / total >= 0.5;
        }

        static string Norm(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /* ------------------------------------------------------------------ LRC */

        public static LyricDoc ParseLrc(string lrc)
        {
            if (lrc == null) return null;

            List<double> times = new List<double>();
            List<string> texts = new List<string>();

            string[] lines = lrc.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Trim().Length == 0) continue;

                MatchCollection ms = TimeTag.Matches(line);
                if (ms.Count == 0) continue;

                string text = TimeTag.Replace(line, "").Trim();
                if (text.Length == 0) continue;          /* 纯时间戳的空行跳过 */

                for (int k = 0; k < ms.Count; k++)
                {
                    Match m = ms[k];
                    double mm = double.Parse(m.Groups[1].Value);
                    double ss = double.Parse(m.Groups[2].Value);
                    double frac = 0;
                    if (m.Groups[3].Success)
                    {
                        string f = m.Groups[3].Value;
                        frac = double.Parse(f) / Math.Pow(10, f.Length);
                    }
                    times.Add(mm * 60.0 + ss + frac);
                    texts.Add(text);
                }
            }

            if (texts.Count == 0) return null;

            /* 时间戳在天生乱序的歌词里会出现，排一下再线性找。
               这里排序的是 times 的**副本**，真正要的是 order 这个置换 ——
               Array.Sort(keys, items) 会把 items 按 keys 重排。 */
            int[] order = new int[times.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort((double[])times.ToArray(), order);

            double[] t2 = new double[times.Count];
            string[] s2 = new string[texts.Count];
            for (int i = 0; i < order.Length; i++)
            {
                t2[i] = times[order[i]];
                s2[i] = texts[order[i]];
            }

            LyricDoc doc = new LyricDoc();
            doc.Times = t2;
            doc.Texts = s2;
            return doc;
        }

        /* ----------------------------------------------------------------- HTTP */

        static string Get(string url) { return Get(url, null); }

        static string Get(string url, string referer)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = UA;
            req.Timeout = TimeoutMs;
            req.ReadWriteTimeout = TimeoutMs;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (referer != null) req.Referer = referer;

            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream st = resp.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buf = new byte[8192];
                    int n;
                    while ((n = st.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    /* 一律按 UTF-8 解：中文歌词被当成 latin-1 会变成乱码 */
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            catch (WebException)
            {
                /* 404 = 这首没收录 / 400 = 参数不合法 / 429 = 限流，一律交给下一个源。
                   429 由调用方的重试 TTL 兜住（见 Media.EnsureLyric）。 */
                return null;
            }
        }

        /* ------------------------------------------------------- 迷你 JSON 取值 */

        /* 没有 Newtonsoft，也不可能为取几个字段引依赖。

           这里刻意不用「找第一个 "key":」那种写法 —— 那正是网易云 id 那个 bug
           的来源：歌曲对象里 album.artist.id 排在自己的 id 前面，按「第一次出现」
           取必然取错。所以按**花括号深度**只认对象顶层的字段。 */
        static bool TopField(string obj, string key, out string value)
        {
            value = null;
            if (obj == null || obj.Length == 0) return false;

            int n = obj.Length;
            int depth = 0;
            int i = 0;

            while (i < n)
            {
                char c = obj[i];

                if (c == '{') { depth++; i++; continue; }
                if (c == '}') { depth--; i++; continue; }
                if (c != '"') { i++; continue; }

                /* 字符串：可能是一个键名，也可能是一个值 */
                int keyStart = i;
                int after = SkipString(obj, i);

                if (depth != 1) { i = after; continue; }

                int j = after;
                while (j < n && IsWs(obj[j])) j++;

                if (j >= n || obj[j] != ':')
                {
                    i = after;
                    continue;
                }

                /* depth == 1 且后面跟冒号 —— 这就是一个顶层键 */
                string name = Unescape(obj.Substring(keyStart + 1, after - keyStart - 2));

                j++;
                while (j < n && IsWs(obj[j])) j++;
                if (j >= n) return false;

                if (name != key)
                {
                    i = after;
                    continue;
                }

                /* 找到目标键，读它的值 */
                if (obj[j] == '"')
                {
                    int ve = SkipString(obj, j);
                    value = Unescape(obj.Substring(j + 1, ve - j - 2));
                    return true;
                }

                if (obj[j] == '{' || obj[j] == '[') return false;   /* 不取嵌套结构 */

                int vs = j;
                while (j < n && ",}]".IndexOf(obj[j]) < 0) j++;
                value = obj.Substring(vs, j - vs).Trim();
                return value.Length > 0;
            }
            return false;
        }

        static bool IsWs(char c)
        {
            return c == ' ' || c == '\t' || c == '\r' || c == '\n';
        }

        /* s[i] == '"'，返回闭合引号**之后**的位置 */
        static int SkipString(string s, int i)
        {
            i++;
            while (i < s.Length)
            {
                if (s[i] == '\\') { i += 2; continue; }
                if (s[i] == '"') return i + 1;
                i++;
            }
            return s.Length;
        }

        /* 反转义一个已去掉外层引号的字符串内容 */
        static string Unescape(string raw)
        {
            if (raw.IndexOf('\\') < 0) return raw;

            StringBuilder sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c != '\\') { sb.Append(c); continue; }
                if (i + 1 >= raw.Length) break;

                char e = raw[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '/': sb.Append('/'); break;
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case 'u':
                        if (i + 4 < raw.Length)
                        {
                            int code;
                            if (int.TryParse(raw.Substring(i + 1, 4), NumberStyles.HexNumber,
                                             CultureInfo.InvariantCulture, out code))
                                sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        /* 取出 key 对应数组/对象的原文（含首尾括号） */
        static string SliceValue(string json, string key, char open, char close)
        {
            if (json == null) return null;

            int at = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (at < 0) return null;

            int from = json.IndexOf(open, at);
            if (from < 0) return null;

            int depth = 0;
            int i = from;
            while (i < json.Length)
            {
                char c = json[i];

                if (c == '"')
                {
                    i = SkipString(json, i);
                    continue;
                }
                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return json.Substring(from, i - from + 1);
                }
                i++;
            }
            return null;
        }

        /* 把一段 JSON 拆成顶层对象的原文。
           跳过字符串字面量（含转义），按花括号深度切分。 */
        static List<string> TopLevelObjects(string json)
        {
            List<string> list = new List<string>();
            if (json == null) return list;

            int depth = 0;
            int start = -1;
            int i = 0;

            while (i < json.Length)
            {
                char c = json[i];

                if (c == '"')
                {
                    i = SkipString(json, i);
                    continue;
                }
                if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth <= 0 && start >= 0)
                    {
                        list.Add(json.Substring(start, i - start + 1));
                        start = -1;
                        depth = 0;
                    }
                }
                i++;
            }
            return list;
        }
    }
}