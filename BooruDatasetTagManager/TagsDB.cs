using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Translator.Crypto;

namespace BooruDatasetTagManager
{
    [Serializable]
    public class TagsDB
    {
        public int Version;
        public bool FixTags;
        public List<TagItem> Tags;
        public Dictionary<string, long> LoadedFiles;
        public Dictionary<long, int> hashes;
        private const int curVersion = 101;

        public TagsDB()
        {
            Version = curVersion;
            Tags = new List<TagItem>();
            LoadedFiles = new Dictionary<string, long>();
            hashes = new Dictionary<long, int>();
        }

        private string[] ReadAllLines(byte[] data, Encoding encoding)
        {
            
            List<string> list = new List<string>();
            using (MemoryStream ms = new MemoryStream(data))
            {
                using (StreamReader streamReader = new StreamReader(ms, encoding))
                {
                    string item;
                    while ((item = streamReader.ReadLine()) != null)
                    {
                        list.Add(item);
                    }
                }
            }
            return list.ToArray();
        }

        public void ClearDb()
        {
            Tags.Clear();
            hashes.Clear();
        }

        public void SetNeedFixTags(bool fixTags)
        {
            FixTags = fixTags;
        }

        public void ResetVersion()
        {
            Version = curVersion;
        }

        public void ClearLoadedFiles()
        {
            LoadedFiles.Clear();
        }

        public void LoadCSVFromDir(string dir)
        {
            FileInfo[] csvFiles = new DirectoryInfo(dir).GetFiles("*.csv", SearchOption.TopDirectoryOnly);
            // 使用顺序处理而不是并行处理以避免CPU占用过高
            foreach (FileInfo item in csvFiles)
            {
                LoadFromCSVFile(item.FullName);
            }
        }

        public void LoadTxtFromDir(string dir)
        {
            FileInfo[] txtFiles = new DirectoryInfo(dir).GetFiles("*.txt", SearchOption.TopDirectoryOnly);
            // 使用顺序处理而不是并行处理以避免CPU占用过高
            foreach (FileInfo item in txtFiles)
            {
                LoadFromTxtFile(item.FullName);
            }
        }

        public void LoadFromTxtFile(string fPath, bool append = true)
        {
            byte[] data = File.ReadAllBytes(fPath);
            long hash = Adler32.GenerateHash(data);
            string fName = Path.GetFileName(fPath);
            if (LoadedFiles.ContainsKey(fName))
            {
                if (LoadedFiles[fName] == hash)
                    return;
                else
                    LoadedFiles[fName] = hash;
            }
            else
            {
                LoadedFiles.Add(fName, hash);
            }


            string[] lines = ReadAllLines(data, Encoding.UTF8);
            if (!append)
                ClearDb();
            
            // 批量处理所有标签以提高性能
            var newTags = new List<(string tag, int count, bool isAlias, string parent)>();
            foreach (string item in lines)
            {
                if (!string.IsNullOrWhiteSpace(item))
                {
                    string tag = PrepareTag(item.Trim().ToLower());
                    if (!string.IsNullOrEmpty(tag))
                    {
                        newTags.Add((tag, 0, false, null));
                    }
                }
            }
            
            // 批量添加标签以减少锁竞争
            ProcessBatchTags(newTags);
        }


        public void LoadFromCSVFile(string fPath, bool append = true)
        {
            // 使用更高效的字符串分割替代正则表达式
            byte[] data = File.ReadAllBytes(fPath);
            long hash = Adler32.GenerateHash(data);
            string fName = Path.GetFileName(fPath);
            if (LoadedFiles.ContainsKey(fName))
            {
                if (LoadedFiles[fName] == hash)
                    return;
                else
                    LoadedFiles[fName] = hash;
            }
            else
            {
                LoadedFiles.Add(fName, hash);
            }


            string[] lines = ReadAllLines(data, Encoding.UTF8);
            if (!append)
                Tags.Clear();
                
            // 批量处理所有CSV行以提高性能
            var newTags = new List<(string tag, int count, bool isAlias, string parent)>();
            
            foreach (string item in lines)
            {
                // 使用更高效的字符串解析替代正则表达式
                var parsed = ParseCSVLine(item);
                if (parsed != null)
                {
                    string tagName = parsed.Item1;
                    int count = parsed.Item2;
                    string[] aliases = ParseAliases(parsed.Item3); // aliases string
                    
                    if (!string.IsNullOrEmpty(tagName))
                    {
                        newTags.Add((PrepareTag(tagName.Trim().ToLower()), count, false, null));
                        
                        foreach (var al in aliases)
                        {
                            if (!string.IsNullOrEmpty(al))
                            {
                                newTags.Add((PrepareTag(al.Trim().ToLower()), count, true, tagName));
                            }
                        }
                    }
                }
            }
            
            // 批量添加标签以减少锁竞争
            ProcessBatchTags(newTags);
        }

        // 批量处理标签以提高性能
        private void ProcessBatchTags(List<(string tag, int count, bool isAlias, string parent)> newTags)
        {
            lock (hashes)
            {
                foreach (var (tag, count, isAlias, parent) in newTags)
                {
                    if (string.IsNullOrWhiteSpace(tag))
                        continue;
                        
                    long tagHash = tag.GetHash();

                    // 检查是否已存在
                    if (hashes.TryGetValue(tagHash, out int existTagIndex))
                    {
                        // 如果存在，增加计数
                        Tags[existTagIndex].Count += count;
                    }
                    else
                    {
                        // 如果不存在，创建新项
                        var tagItem = new TagItem();
                        tagItem.SetTag(tag);
                        tagItem.Count = count;
                        tagItem.IsAlias = isAlias;
                        tagItem.Parent = PrepareTag(parent);
                        
                        hashes.Add(tagHash, Tags.Count);
                        Tags.Add(tagItem);
                    }
                }
            }
        }

        // 更高效的CSV行解析方法
        private Tuple<string, int, string> ParseCSVLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            
            int firstComma = line.IndexOf(',');
            if (firstComma == -1) return null;
            
            string tagName = line.Substring(0, firstComma);
            
            int secondComma = FindNextComma(line, firstComma + 1);
            if (secondComma == -1) return null;
            
            string countStr = line.Substring(firstComma + 1, secondComma - firstComma - 1);
            
            int thirdComma = FindNextComma(line, secondComma + 1);
            if (thirdComma == -1) return null;
            
            string count2Str = line.Substring(secondComma + 1, thirdComma - secondComma - 1);
            
            string aliasesStr = line.Substring(thirdComma + 1);
            
            if (!int.TryParse(count2Str, out int count)) return null;
            
            return new Tuple<string, int, string>(tagName, count, aliasesStr);
        }

        private int FindNextComma(string line, int startIndex)
        {
            int index = startIndex;
            bool inQuotes = false;
            
            while (index < line.Length)
            {
                char c = line[index];
                
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    return index;
                }
                
                index++;
            }
            
            return -1;
        }

        private string[] ParseAliases(string aliasesStr)
        {
            if (string.IsNullOrEmpty(aliasesStr))
                return new string[0];
                
            // 移除首尾的引号（如果有）
            if (aliasesStr.StartsWith("\"") && aliasesStr.EndsWith("\""))
            {
                aliasesStr = aliasesStr.Substring(1, aliasesStr.Length - 2);
            }
            
            if (string.IsNullOrEmpty(aliasesStr))
                return new string[0];
                
            // 分割并清理别名
            var parts = aliasesStr.Split(',');
            var result = new List<string>();
            
            foreach (var part in parts)
            {
                string cleanPart = part.Trim();
                if (!string.IsNullOrEmpty(cleanPart))
                {
                    result.Add(cleanPart);
                }
            }
            
            return result.ToArray();
        }

        public void SortTags()
        {
            Tags.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        }

        private string PrepareTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return tag;
            if (FixTags)
            {
                tag = tag.Replace('_', ' ');
                tag = tag.Replace("\\(", "(");
                tag = tag.Replace("\\)", ")");
            }
            return tag;
        }

        public bool IsNeedUpdate(string dirToCheck)
        {
            if (Version != curVersion)
                return true;
            if (Program.Settings.FixTagsOnSaveLoad != FixTags)
                return true;
            FileInfo[] tagFiles = new DirectoryInfo(dirToCheck).GetFiles("*.csv", SearchOption.TopDirectoryOnly).
                Concat(new DirectoryInfo(dirToCheck).GetFiles("*.txt", SearchOption.TopDirectoryOnly)).ToArray();
            if (tagFiles.Length == 0)
                return false;
            if (LoadedFiles.Count != tagFiles.Length)
                return true;
            foreach (var item in tagFiles)
            {
                byte[] data = File.ReadAllBytes(item.FullName);
                long hash = Adler32.GenerateHash(data);
                if (!LoadedFiles.ContainsKey(item.Name))
                    return true;
                if(LoadedFiles[item.Name]!=hash) 
                    return true;
            }
            return false;
        }

        public void LoadTranslation(TranslationManager transManager)
        {
            bool onlyManual = Program.Settings.OnlyManualTransInAutocomplete;
            
            var translationCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var transItem in transManager.Translations)
            {
                if (!onlyManual || transItem.IsManual == onlyManual)
                {
                    translationCache[transItem.Orig] = transItem.Trans;
                }
            }
            
            foreach (var tag in Tags)
            {
                if (translationCache.TryGetValue(tag.Tag, out var translation))
                {
                    tag.Translation = translation;
                }
                else
                {
                    tag.Translation = null;
                }
            }
        }

        public static TagsDB LoadFromTagFile(string fPath)
        {
            if (File.Exists(fPath))
            {
                try
                {
                    return (TagsDB)Extensions.LoadDataSet(File.ReadAllBytes(fPath));
                }
                catch (Exception)
                {
                    return null;
                }
            }
            else
                return null;
        }

        public void SaveTags(string fPath)
        {
            Extensions.SaveDataSet(this, fPath);
        }

        [Serializable]
        public class TagItem
        {
            public string Tag { get; private set; }
            public long TagHash { get; private set; }
            public int Count;
            //public List<string> Aliases;
            public bool IsAlias;
            public string Parent;

            public string Translation;

            public TagItem()
            {
                //Aliases = new List<string>();
            }

            public void SetTag(string tag)
            {
                Tag = tag.Trim().ToLower();
                TagHash = Tag.GetHash();
            }

            public string GetTag()
            {
                if (IsAlias)
                    return Parent;
                else
                    return Tag;
            }

            public override string ToString()
            {
                if (IsAlias)
                    return $"{Tag} -> {Parent}{$" ({Count})"}{(string.IsNullOrEmpty(Translation) ? "" : $" [{Translation}]")}";
                else
                    return $"{Tag}{$" ({Count})"}{(string.IsNullOrEmpty(Translation) ? "" : $" [{Translation}]")}";
            }
        }

    }
}