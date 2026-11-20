using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

namespace WinIsland
{
    sealed class PetClipGroup
    {
        public readonly string Name;
        public readonly string[] Clips;
        public PetClipGroup(string name, params string[] clips) { Name=name; Clips=clips; }
    }

    static class ReferenceClips
    {
        public const double DefaultZoom = 0.6;
        static readonly Dictionary<string,PetClip> Clips = Load();
        public static readonly PetClip[] All = new List<PetClip>(Clips.Values).ToArray();
        public static readonly PetClip[] Idle = new PetClip[] {
            Clips["Idle_A"], Clips["Idle_B"], Clips["Idle_D"]
        };
        public static readonly PetClipGroup[] Groups = new PetClipGroup[] {
            new PetClipGroup("Blocked", "Blocked_A", "Blocked_B", "Blocked_C"),
            new PetClipGroup("Bodymoves", "Bodymoves_A"),
            new PetClipGroup("Done", "Done_A", "Done_B", "Done_C"),
            new PetClipGroup("Idle", "Idle_A", "Idle_B", "Idle_D"),
            new PetClipGroup("Logo", "Logo_A", "Logo_B"),
            new PetClipGroup("Marketing", "Marketing_A", "Marketing_B", "Marketing_C", "Marketing_D"),
            new PetClipGroup("Rotation", "Rotation_A", "Rotation_B", "Rotation_C"),
            new PetClipGroup("Streaming", "Streaming_A", "Streaming_B", "Streaming_C"),
            new PetClipGroup("Working", "Working_A", "Working_B", "Working_C", "Working_D", "Working_E", "Working_F")
        };

        public static PetClip Find(string name)
        {
            PetClip clip;
            return name != null && Clips.TryGetValue(name, out clip) ? clip : null;
        }

        static Dictionary<string,PetClip> Load()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream("WinIsland.grok-clips.json"))
            {
                if (stream == null) throw new InvalidDataException("Embedded reference clips are missing.");
                using (StreamReader reader = new StreamReader(stream))
                {
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    IDictionary<string,object> root = (IDictionary<string,object>)serializer.DeserializeObject(reader.ReadToEnd());
                    IList rows = (IList)root["clips"];
                    Dictionary<string,PetClip> clips = new Dictionary<string,PetClip>(StringComparer.Ordinal);
                    foreach (object item in rows)
                    {
                        IDictionary<string,object> row = (IDictionary<string,object>)item;
                        PetClip clip = new PetClip();
                        clip.Name = (string)row["name"];
                        clip.Duration = Number(row["duration"]);
                        IDictionary<string,object> tracks = (IDictionary<string,object>)row["tracks"];
                        clip.Position = ReadTrack(tracks, "position");
                        clip.Rotation = ReadTrack(tracks, "rotation");
                        clip.Squash = ReadTrack(tracks, "squash");
                        clip.Scale = ReadTrack(tracks, "scale");
                        clip.Lids = ReadTrack(tracks, "lids");
                        List<double> blinks = new List<double>();
                        List<PetFaceKey> expressions = new List<PetFaceKey>();
                        IList faceTrack;
                        if (tracks.ContainsKey("expression") && (faceTrack = tracks["expression"] as IList) != null)
                        {
                            foreach (object faceItem in faceTrack)
                            {
                                IDictionary<string,object> key = (IDictionary<string,object>)faceItem;
                                string value = Convert.ToString(key["v"]);
                                double time = Number(key["t"]);
                                if (value == "blink") blinks.Add(time);
                                else
                                {
                                    PetExpression expression;
                                    if (TryExpression(value, out expression))
                                        expressions.Add(new PetFaceKey(time, expression));
                                }
                            }
                        }
                        clip.BlinkTimes = blinks.ToArray();
                        clip.Expressions = expressions.ToArray();
                        clips.Add(clip.Name, clip);
                    }
                    if (clips.Count != 28) throw new InvalidDataException("Expected 28 reference animation clips.");
                    return clips;
                }
            }
        }

        static PetTrack ReadTrack(IDictionary<string,object> tracks, string name)
        {
            if (!tracks.ContainsKey(name)) return null;
            IList rows = tracks[name] as IList;
            if (rows == null || rows.Count == 0) return null;
            PetKey[] keys = new PetKey[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                IDictionary<string,object> row = (IDictionary<string,object>)rows[i];
                IList values = row["v"] as IList;
                if (values == null) throw new InvalidDataException("Invalid numeric key in " + name + ".");
                double[] value = new double[values.Count];
                for (int j = 0; j < value.Length; j++) value[j] = Number(values[j]);
                IList easing = row["ease"] as IList;
                PetEase ease = easing == null || easing.Count != 4 ? PetEase.Smooth
                    : new PetEase(Number(easing[0]), Number(easing[1]), Number(easing[2]), Number(easing[3]));
                keys[i] = new PetKey(Number(row["t"]), ease, value);
            }
            return new PetTrack(keys);
        }

        /* 表情名归一。

           上游（grok-player 的 Mb）不是「认识的留下、不认识的丢掉」，而是
           **认不出来的一律归一成 neutral**；neutral / calm 本身就是合法表情名
           （见 grok-recipes.json 的 expressionNames：
            neutral, happy, surprised, sad, sleepy, squinted, calm, inspecting, confused）。

           这里以前把 neutral / calm 当成「不认识」直接 return false，调用方就不往
           Expressions 里加这个关键帧。后果不是「表情少一点点」，而是**表情回不去**：
             * Done_A / Blocked_A / Working_F … 的最后一帧是 neutral，
               被丢掉以后整段停在 happy / confused / surprised 上；
             * Marketing_B/C 与 Working_D 整段只有 neutral→calm，
               被丢掉以后这段的 calm 直接读成 neutral。
           实测 28 段里有 8 段表情帧错、3 段整段 calm 丢失（见 shots/pet-integration/
           anim/expression-mapping.csv）。所以这里必须把这些名字都收进来。

           别名按上游 expressionAliases 归一：
             strange→inspecting, inspect→inspecting, squint→squinted,
             thinking→sad, smile→happy, sleep→sleepy, angry→neutral, idle→neutral。 */
        static bool TryExpression(string name, out PetExpression expression)
        {
            switch (name)
            {
                case "neutral": case "idle": case "angry":
                    expression = PetExpression.Neutral; return true;
                case "happy": case "smile":
                    expression = PetExpression.Happy; return true;
                case "surprised":
                    expression = PetExpression.Surprised; return true;
                case "sad": case "thinking":
                    expression = PetExpression.Sad; return true;
                case "sleepy": case "sleep":
                    expression = PetExpression.Sleepy; return true;
                case "squinted": case "squint":
                    expression = PetExpression.Angry; return true;
                case "calm":
                    expression = PetExpression.Calm; return true;
                case "inspecting": case "inspect": case "strange":
                    expression = PetExpression.Focused; return true;
                case "confused":
                    expression = PetExpression.Curious; return true;
                default:
                    /* 上游对未知名字的处理就是 neutral，此处照抄，不再丢帧 */
                    expression = PetExpression.Neutral; return true;
            }
        }

        static double Number(object value) { return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture); }
    }
}
