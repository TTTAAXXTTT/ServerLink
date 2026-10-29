using System;
using System.Collections.Generic;
using System.Text;
namespace ServerLink { public static class MessageProof {
        public static bool Matches(string displayed,string submitted)
        {
            // Claude's rendered paragraphs collapse repeated/edge blank lines.
            // Only compare a newly indexed user message; do not change submitted
            // text or use this comparison when deciding to clear an edited draft.
            if(NormalizeDisplayedMessage(displayed)==NormalizeDisplayedMessage(submitted))return true;
            return MatchesRenderedCode(displayed,submitted);
        }
        static bool MatchesRenderedCode(string displayed,string submitted)
        {
            var parts=new List<string>();var outside=new List<string>();var code=new List<string>();char fence='\0';int fenceLength=0;bool found=false;
            foreach(string line in (submitted??"").Replace("\r\n","\n").Split('\n'))
            {
                var marker=System.Text.RegularExpressions.Regex.Match(line,@"^[ ]{0,3}(`{3,}|~{3,})(.*)$");
                if(fence!='\0')
                {
                    if(marker.Success&&marker.Groups[1].Value[0]==fence&&marker.Groups[1].Value.Length>=fenceLength&&String.IsNullOrWhiteSpace(marker.Groups[2].Value))
                    {
                        // The accessible body omits the fence/language, but its
                        // code text, indentation and internal blank lines must match.
                        parts.Add(System.Text.RegularExpressions.Regex.Escape(String.Join("\n",code)));code.Clear();fence='\0';found=true;
                    }
                    else code.Add(line);
                    continue;
                }
                if(marker.Success)
                {
                    string text=NormalizeDisplayedMessage(String.Join("\n",outside));if(text.Length>0)parts.Add(ParagraphPattern(text));outside.Clear();fence=marker.Groups[1].Value[0];fenceLength=marker.Groups[1].Value.Length;
                }
                else outside.Add(line);
            }
            if(!found||fence!='\0')return false;
            string tail=NormalizeDisplayedMessage(String.Join("\n",outside));if(tail.Length>0)parts.Add(ParagraphPattern(tail));
            string pattern=@"\A\n*"+String.Join(@"\n{1,2}",parts)+@"\n*\z";
            try{return System.Text.RegularExpressions.Regex.IsMatch((displayed??"").Replace("\r\n","\n"),pattern,System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));}
            catch(System.Text.RegularExpressions.RegexMatchTimeoutException){return false;}
        }
        static string ParagraphPattern(string text)
        {
            var result=new StringBuilder();string[] lines=text.Split('\n');
            for(int i=0;i<lines.Length;i++)
            {
                if(i>0)result.Append("\\n");
                if(lines[i]==""){result.Append(@"(?:[ \t]*\n)+");i++;if(i==lines.Length)break;}
                result.Append(System.Text.RegularExpressions.Regex.Escape(lines[i]));
            }
            return result.ToString();
        }
        static string NormalizeDisplayedMessage(string text)
        {
            var lines=new List<string>();char fence='\0';int fenceLength=0;
            foreach(string line in (text??"").Replace("\r\n","\n").Split('\n'))
            {
                var marker=System.Text.RegularExpressions.Regex.Match(line,@"^[ ]{0,3}(`{3,}|~{3,})(.*)$");
                if(fence!='\0')
                {
                    lines.Add(line);
                    if(marker.Success&&marker.Groups[1].Value[0]==fence&&marker.Groups[1].Value.Length>=fenceLength&&String.IsNullOrWhiteSpace(marker.Groups[2].Value))fence='\0';
                    continue;
                }
                if(marker.Success){fence=marker.Groups[1].Value[0];fenceLength=marker.Groups[1].Value.Length;lines.Add(line);continue;}
                if(String.IsNullOrWhiteSpace(line)){if(lines.Count>0&&lines[lines.Count-1]!="")lines.Add("");}
                else lines.Add(line);
            }
            if(fence=='\0'&&lines.Count>0&&lines[lines.Count-1]=="")lines.RemoveAt(lines.Count-1);
            return String.Join("\n",lines);
        }

} }
