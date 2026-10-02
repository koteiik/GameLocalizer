#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
namespace GameLocalizer.RuntimeCollector
{
    public sealed class CaptureBuffer
    {
        private readonly object gate = new object(), writeGate = new object();
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private readonly HashSet<string> dirty = new HashSet<string>();
        private readonly string path;
        public CaptureBuffer(string path) { this.path = path; }
        public static bool Accept(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 16384) return false;
            return !Regex.IsMatch(text.Trim(), @"^(?:[\d\s.,:%+\-]+|\d{1,2}:\d{2}(?::\d{2})?|(?:FPS\s*[:=]?\s*\d+(?:\.\d+)?|\d+\s*FPS)|(?:ObjectID|InstanceID)\s*[:=]\s*\d+)$", RegexOptions.IgnoreCase);
        }
        public void Observe(string text, string scene, string obj, string hierarchy, string type, string assembly)
        {
            if (!Accept(text)) return;
            var key = Quote(text) + Quote(scene) + Quote(hierarchy) + Quote(type);
            lock (gate)
            {
                Entry entry;
                if (!entries.TryGetValue(key, out entry))
                {
                    if (entries.Count >= 100000) return;
                    entry = new Entry { Text=text, Scene=scene, Object=obj, Hierarchy=hierarchy, Component=type, Assembly=assembly, FirstSeen=DateTime.UtcNow.ToString("O") }; entries.Add(key, entry);
                }
                entry.LastSeen=DateTime.UtcNow.ToString("O"); entry.SeenCount++; dirty.Add(key);
            }
        }
        public void Flush()
        {
            lock (writeGate)
            {
                string[] keys; string[] lines;
                lock (gate) { keys=dirty.ToArray(); lines=keys.Select(k=>entries[k].Json()).ToArray(); dirty.Clear(); }
                if (lines.Length == 0) return;
                try
                {
                    using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    { foreach(var line in lines) writer.WriteLine(line); writer.Flush(); stream.Flush(true); }
                }
                catch { lock(gate) foreach(var key in keys) dirty.Add(key); throw; }
            }
        }
        public static string Quote(string value)
        {
            var result = new StringBuilder("\""); foreach(var c in value ?? "")
            { if(c=='"' || c=='\\') result.Append('\\').Append(c); else if(c<32) result.Append("\\u").Append(((int)c).ToString("x4")); else result.Append(c); } return result.Append('"').ToString();
        }
        private sealed class Entry
        {
            public string Text,Scene,Object,Hierarchy,Component,Assembly,FirstSeen,LastSeen; public long SeenCount;
            public string Json() { return "{\"Text\":"+Quote(Text)+",\"Scene\":"+Quote(Scene)+",\"Object\":"+Quote(Object)+",\"Hierarchy\":"+Quote(Hierarchy)+",\"Component\":"+Quote(Component)+",\"Assembly\":"+Quote(Assembly)+",\"FirstSeen\":"+Quote(FirstSeen)+",\"LastSeen\":"+Quote(LastSeen)+",\"SeenCount\":"+SeenCount+"}"; }
        }
    }
}

