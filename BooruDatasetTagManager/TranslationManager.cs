using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Translator.Crypto;
using static System.Net.Mime.MediaTypeNames;

namespace BooruDatasetTagManager
{
    public class TranslationManager
    {
        private string _language;
        private string _workDir;
        public List<TransItem> Translations { get; set; }
        private AbstractTranslator translator;
        private string translationFilePath;
        private Dictionary<string, TransItem> _translationDict;
        private bool _offlineMode;
        private bool _isCsvFormat;

        public TranslationManager(string toLang, TranslationService service, string workDir, bool offlineMode = false, string customTranslationFile = "")
        {
            _language = toLang;
            _workDir = workDir;
            Translations = new List<TransItem>();
            _translationDict = new Dictionary<string, TransItem>(StringComparer.OrdinalIgnoreCase);
            _offlineMode = offlineMode;
            translator = AbstractTranslator.Create(service);
            if (!string.IsNullOrEmpty(customTranslationFile) && File.Exists(customTranslationFile))
            {
                translationFilePath = customTranslationFile;
            }
            else
            {
                translationFilePath = Path.Combine(_workDir, _language + ".txt");
            }
            _isCsvFormat = Path.GetExtension(translationFilePath).Equals(".csv", StringComparison.OrdinalIgnoreCase);
        }

        public void LoadTranslations()
        {
            if (!File.Exists(translationFilePath))
            {
                var sw = File.CreateText(translationFilePath);
                if (_isCsvFormat)
                {
                    sw.WriteLine("//Translation format: <original>,<translation>");
                }
                else
                {
                    sw.WriteLine("//Translation format: <original>=<translation>");
                }
                sw.Dispose();
                return;
            }
            string[] lines = File.ReadAllLines(translationFilePath);
            foreach (var item in lines)
            {
                if (item.Trim().StartsWith("//"))
                    continue;
                
                bool manual = false;
                string processLine = item;
                if (processLine.StartsWith("*"))
                {
                    processLine = processLine.Substring(1);
                    manual = true;
                }
                
                string orig;
                string trans;
                
                if (_isCsvFormat)
                {
                    int commaIndex = processLine.LastIndexOf(',');
                    if (commaIndex == -1)
                        continue;
                    
                    orig = processLine.Substring(0, commaIndex).Trim();
                    trans = processLine.Substring(commaIndex + 1).Trim();
                    orig = orig.Replace("_", " ");
                }
                else
                {
                    int index = processLine.LastIndexOf('=');
                    if (index == -1)
                        continue;
                    
                    orig = processLine.Substring(0, index).Trim();
                    trans = processLine.Substring(index + 1).Trim();
                }
                
                if (!string.IsNullOrEmpty(orig) && !_translationDict.ContainsKey(orig))
                {
                    var newItem = new TransItem(orig, trans, manual, false);
                    Translations.Add(newItem);
                    _translationDict[orig] = newItem;
                }
            }
        }

        public void ConvertCsvToTxt()
        {
            if (!_isCsvFormat)
                return;

            string txtFilePath = Path.Combine(_workDir, _language + ".txt");
            if (File.Exists(txtFilePath))
            {
                File.Delete(txtFilePath);
            }

            var sw = File.CreateText(txtFilePath);
            sw.WriteLine("//Translation format: <original>=<translation>");
            
            foreach (var transItem in Translations)
            {
                string line;
                if (transItem.IsManual)
                {
                    line = $"*{transItem.Orig}={transItem.Trans}";
                }
                else
                {
                    line = $"{transItem.Orig}={transItem.Trans}";
                }
                sw.WriteLine(line);
            }
            sw.Dispose();
            
            _isCsvFormat = false;
            translationFilePath = txtFilePath;
            
            Translations.Clear();
            _translationDict.Clear();
            LoadTranslations();
        }

        public bool Contains(string orig)
        {
            return _translationDict.ContainsKey(orig);
        }

        public bool Contains(long hash)
        {
            return Translations.Any(t => t.OrigHash == hash);
        }

        public string GetTranslation(string text)
        {
            if (_translationDict.TryGetValue(text, out var item))
            {
                return item.Trans;
            }
            return null;
        }

        public string GetTranslation(string text, bool onlyManual)
        {
            if (onlyManual)
            {
                var res = Translations.FirstOrDefault(t => t.Orig.Equals(text, StringComparison.OrdinalIgnoreCase) && t.IsManual == onlyManual);
                return res?.Trans;
            }
            else
            {
                if (_translationDict.TryGetValue(text, out var item))
                {
                    return item.Trans;
                }
                return null;
            }
        }

        public async Task<string> GetTranslationAsync(string text)
        {
            return await Task.Run(() =>
            {
                return GetTranslation(text);
            });
        }

        public void AddTranslation(string orig, string trans, bool isManual)
        {
            string line;
            if (_isCsvFormat)
            {
                string normalizedOrig = orig.Replace(" ", "_");
                line = $"{(isManual ? "*" : "")}{normalizedOrig},{trans}";
            }
            else
            {
                line = $"{(isManual ? "*" : "")}{orig}={trans}";
            }
            File.AppendAllText(translationFilePath, line + "\r\n", Encoding.UTF8);
            var newItem = new TransItem(orig, trans, isManual, false);
            Translations.Add(newItem);
            _translationDict[orig] = newItem;
        }

        public async Task AddTranslationAsync(string orig, string trans, bool isManual)
        {
            StreamWriter sw = new StreamWriter(translationFilePath, true, Encoding.UTF8);
            string line;
            if (_isCsvFormat)
            {
                string normalizedOrig = orig.Replace(" ", "_");
                line = $"{(isManual ? "*" : "")}{normalizedOrig},{trans}";
            }
            else
            {
                line = $"{(isManual ? "*" : "")}{orig}={trans}";
            }
            await sw.WriteLineAsync(line);
            sw.Close();
            var newItem = new TransItem(orig, trans, isManual, false);
            Translations.Add(newItem);
            _translationDict[orig] = newItem;
        }

        public async Task<string> TranslateAsync(string text)
        {
            await Program.TranslationLocker.WaitAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                Program.TranslationLocker.Release();
                return string.Empty;
            }
            string result = await GetTranslationAsync(text);
            if (result != null)
            {
                Program.TranslationLocker.Release();
                return result;
            }
            
            if (_offlineMode)
            {
                Program.TranslationLocker.Release();
                return string.Empty;
            }
            
            result = await translator.TranslateAsync(text, "en", _language);
            if (!string.IsNullOrEmpty(result))
                await AddTranslationAsync(text, result, false);
            Program.TranslationLocker.Release();
            return result;
        }


        public class TransItem
        {
            public string Orig { get; private set; }
            public string Trans {get; set; }
            public long OrigHash { get; private set; }
            public bool IsManual { get; private set; }

            public TransItem(string orig, string trans, bool isManual, bool isCsvFormat = false)
            {
                Orig = orig;
                Trans = trans;
                OrigHash = orig.ToLower().GetHash();
                IsManual = isManual;
            }

            public static TransItem Create(string text, bool isCsvFormat = false)
            {
                bool manual = false;
                if (text.StartsWith("*"))
                {
                    text = text.Substring(1);
                    manual = true;
                }
                
                int index = text.LastIndexOf('=');
                if (index == -1)
                    return null;
                    
                string orig = text.Substring(0, index).Trim();
                string trans = text.Substring(index + 1).Trim();
                
                return new TransItem(orig, trans, manual, false);
            }


            public TransItem() { }
        }
    }
}
