#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace GameLocalizer.RuntimeCollector
{
    [DataContract] public sealed class RuntimeDictionaryDocument
    {
        [DataMember] public int SchemaVersion;
        [DataMember] public string GameId;
        [DataMember] public string GeneratedAt;
        [DataMember] public string Version;
        [DataMember] public int EntryCount;
        [DataMember] public RuntimeDictionaryEntry[] Entries;
    }
    [DataContract] public sealed class RuntimeDictionaryEntry
    {
        [DataMember] public string OriginalText;
        [DataMember] public string RussianText;
    }

    /// <summary>Startup-only JSON I/O. The lookup path has no I/O, model, process, or network APIs.</summary>
    public sealed class RuntimeDictionaryLookup
    {
        private readonly Dictionary<string,string> exact = new Dictionary<string,string>(StringComparer.Ordinal);
        private readonly Dictionary<char,List<Pattern>> patterns = new Dictionary<char,List<Pattern>>();
        private static readonly Regex Marker = new Regex(@"\{\{[A-Z]\}\}");
        public int Count { get { return exact.Count; } }
        public static RuntimeDictionaryLookup Load(string path, string gameId, out string error)
        {
            error=null;
            try
            {
                if(!File.Exists(path)){error="Runtime dictionary missing; translation disabled.";return new RuntimeDictionaryLookup();}
                var file=new FileInfo(path);if(file.Length>64*1024*1024)throw new InvalidDataException("Dictionary exceeds 64 MiB");
                var serializer=new DataContractJsonSerializer(typeof(RuntimeDictionaryDocument),new DataContractJsonSerializerSettings{MaxItemsInObjectGraph=1000000});
                RuntimeDictionaryDocument document;
                using(var stream=File.OpenRead(path)) document=(RuntimeDictionaryDocument)serializer.ReadObject(stream);
                if(document==null || document.SchemaVersion!=1 || document.GameId!=gameId || document.Entries==null || document.EntryCount!=document.Entries.Length || document.EntryCount>100000)
                    throw new InvalidDataException("Invalid runtime dictionary schema/game/count");
                return FromEntries(document.Entries);
            }
            catch(Exception e){error="Runtime dictionary disabled: "+e.Message;return new RuntimeDictionaryLookup();}
        }
        public static RuntimeDictionaryLookup FromEntries(IEnumerable<RuntimeDictionaryEntry> entries)
        {
            var lookup=new RuntimeDictionaryLookup();var conflicts=new HashSet<string>(StringComparer.Ordinal);
            foreach(var entry in entries)
            {
                if(entry==null || !Valid(entry.OriginalText,entry.RussianText))continue;
                string previous;if(lookup.exact.TryGetValue(entry.OriginalText,out previous) && previous!=entry.RussianText)conflicts.Add(entry.OriginalText);
                else lookup.exact[entry.OriginalText]=entry.RussianText;
            }
            foreach(var key in conflicts)lookup.exact.Remove(key);
            var patternCount=0;
            foreach(var entry in lookup.exact)
            {
                Pattern pattern;if(patternCount<128 && TryPattern(entry.Key,entry.Value,out pattern))
                {List<Pattern> bucket;if(!lookup.patterns.TryGetValue(pattern.Prefix[0],out bucket)){bucket=new List<Pattern>();lookup.patterns.Add(pattern.Prefix[0],bucket);}bucket.Add(pattern);patternCount++;}
            }
            return lookup;
        }
        public static bool Valid(string source,string target)
        {
            if(string.IsNullOrWhiteSpace(source)||string.IsNullOrWhiteSpace(target)||source.Length>16384||target.Length>32768)return false;
            // Preserve explicit placeholder tokens and rich-text delimiters before any runtime replacement.
            var left=Regex.Matches(source,@"\{\{[^{}]+\}\}|\{[^{}]+\}|</?[^>\r\n]+>|%(?:\d+\$)?[sdif]|[\r\n\t]");
            var right=Regex.Matches(target,@"\{\{[^{}]+\}\}|\{[^{}]+\}|</?[^>\r\n]+>|%(?:\d+\$)?[sdif]|[\r\n\t]");
            if(left.Count!=right.Count)return false;
            var tokens=new Dictionary<string,int>(StringComparer.Ordinal);
            foreach(Match m in left){int n;tokens.TryGetValue(m.Value,out n);tokens[m.Value]=n+1;}
            foreach(Match m in right){int n;if(!tokens.TryGetValue(m.Value,out n)||n==0)return false;tokens[m.Value]=n-1;}
            foreach(var c in "{}<>"){var a=0;var b=0;foreach(var x in source)if(x==c)a++;foreach(var x in target)if(x==c)b++;if(a!=b)return false;}
            return true;
        }
        public bool TryTranslate(string original,out string translated)
        {
            translated=null;if(original==null)return false;
            if(exact.TryGetValue(original,out translated))return true;
            if(original.Length==0)return false;
            List<Pattern> bucket;if(!patterns.TryGetValue(original[0],out bucket))return false;
            Pattern found=null;int begin=0,length=0;
            foreach(var pattern in bucket)
            {
                if(!original.StartsWith(pattern.Prefix,StringComparison.Ordinal)||!original.EndsWith(pattern.Suffix,StringComparison.Ordinal))continue;
                var size=original.Length-pattern.Prefix.Length-pattern.Suffix.Length;if(size<=0||size>128)continue;
                var safe=true;for(var i=pattern.Prefix.Length;i<pattern.Prefix.Length+size;i++)if(char.IsControl(original[i]) || "{}<>[]".IndexOf(original[i])>=0){safe=false;break;}
                if(!safe)continue;
                // Ambiguous patterns never guess, even if outputs happen to coincide.
                if(found!=null){translated=null;return false;}found=pattern;begin=pattern.Prefix.Length;length=size;
            }
            if(found==null)return false;
            translated=found.Before+original.Substring(begin,length)+found.After;return true;
        }
        private static bool TryPattern(string source,string target,out Pattern pattern)
        {
            pattern=null;var a=Marker.Matches(source);var b=Marker.Matches(target);
            if(a.Count!=1||b.Count!=1||a[0].Value!=b[0].Value||a[0].Index<3)return false;
            // Only a single explicitly authored {{A}}...{{Z}} token. No guessed or regex-generated patterns.
            var prefix=source.Substring(0,a[0].Index);var suffix=source.Substring(a[0].Index+a[0].Length);
            if(prefix.IndexOfAny(new[]{'{','}','<','>','[',']'})>=0||suffix.IndexOfAny(new[]{'{','}','<','>','[',']'})>=0)return false;
            pattern=new Pattern{Prefix=prefix,Suffix=suffix,Before=target.Substring(0,b[0].Index),After=target.Substring(b[0].Index+b[0].Length)};return true;
        }
        private sealed class Pattern{public string Prefix,Suffix,Before,After;}
    }
}
