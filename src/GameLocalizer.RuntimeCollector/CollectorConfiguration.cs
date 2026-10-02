#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
namespace GameLocalizer.RuntimeCollector
{
    public sealed class CollectorConfiguration
    {
        public string GameId { get; private set; }
        public bool DialogueTraceEnabled { get; private set; }
        public CollectorConfiguration(string gameId, bool dialogueTraceEnabled = false)
        {
            if(gameId == null || !Regex.IsMatch(gameId, "^[a-f0-9]{24}$")) throw new InvalidDataException("Invalid collector game ID");
            GameId=gameId; DialogueTraceEnabled=dialogueTraceEnabled;
        }
        public static CollectorConfiguration Parse(IEnumerable<string> lines)
        {
            string id=null; bool trace=false; bool seenTrace=false;
            foreach(var raw in lines)
            {
                var line=raw.Trim().TrimStart('\uFEFF');
                if(line.Length==0 || line.StartsWith("#") || line.StartsWith(";"))continue;
                var separator=line.IndexOf('=');
                if(separator<0) { if(id!=null)throw new InvalidDataException("Duplicate game ID"); id=line; continue; }
                var key=line.Substring(0,separator).Trim();var value=line.Substring(separator+1).Trim();
                if(key.Equals("GameId",StringComparison.OrdinalIgnoreCase)) { if(id!=null)throw new InvalidDataException("Duplicate game ID");id=value; }
                else if(key.Equals("DialogueTraceEnabled",StringComparison.OrdinalIgnoreCase))
                { if(seenTrace || !bool.TryParse(value,out trace))throw new InvalidDataException("Invalid DialogueTraceEnabled");seenTrace=true; }
            }
            return new CollectorConfiguration(id,trace);
        }
        public string Serialize() { return "GameId="+GameId+"\nDialogueTraceEnabled="+(DialogueTraceEnabled?"true":"false")+"\n"; }
    }
}
